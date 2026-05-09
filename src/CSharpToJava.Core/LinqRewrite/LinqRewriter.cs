// Copyright (c) 2016 Michał Komorowski
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// Source: https://github.com/antiufo/roslyn-linq-rewrite

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.LinqRewrite
{
    public partial class LinqRewriter : CSharpSyntaxRewriter
    {
        private SemanticModel semantic;
        private readonly ConversionOptions? _options;

        
        public LinqRewriter(SemanticModel semantic, ConversionOptions? options = null)
        {
            this.semantic = semantic;
            this._options = options;
        }

        /// <summary>
        /// Whether synthesized Java records are available for anonymous types
        /// (Java 25 with UseRecords enabled).
        /// </summary>
        private bool CanUseRecordsForAnonymousTypes =>
            _options != null
            && _options.UseRecords
            && _options.TargetJavaVersion >= JavaVersion.Java25;
        public int RewrittenMethods { get; private set; }
        public int RewrittenLinqQueries { get; private set; }
        public List<string> SkippedLinqChains { get; } = new();
        public LinqRewriteStatistics Statistics { get; } = new();
        static LinqRewriter()
        {

            KnownMethods = typeof(LinqRewriter).GetTypeInfo().DeclaredFields
                .Where(x => x.Name.EndsWith("Method") && x.FieldType == typeof(string))
                .Select(x => (string)x.GetValue(null))
                .ToList();
        }

        private readonly static List<string> KnownMethods;
        public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            return TryCatchVisitInvocationExpression(node, null) ?? base.VisitInvocationExpression(node);
        }

        private bool insideConditionalExpression;
        public override SyntaxNode VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        {
            var old = insideConditionalExpression;
            insideConditionalExpression = true;
            try
            {
                return base.VisitConditionalAccessExpression(node);
            }
            finally
            {
                insideConditionalExpression = old;
            }
            
        }

        private ExpressionSyntax TryCatchVisitInvocationExpression(InvocationExpressionSyntax node, ForEachStatementSyntax containingForEach)
        {
            if (insideConditionalExpression) return null;
            var methodIdx = methodsToAddToCurrentType.Count;
            try
            {
                var k = TryVisitInvocationExpression(node, containingForEach);
                if (k != null)
                {
                    RewrittenLinqQueries++;
                    Statistics.RewrittenChainCount++;
                    return k;
                }
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is NotSupportedException || ex is ArgumentException)
            {
                var removeCount = methodsToAddToCurrentType.Count - methodIdx;
                if (removeCount > 0)
                    methodsToAddToCurrentType.RemoveRange(methodIdx, removeCount);
                var location = node.GetLocation().GetLineSpan();
                var lineNumber = location.StartLinePosition.Line + 1;
                var reason = ex is NotSupportedException
                    ? LinqSkipReason.UnsupportedMethodChain
                    : LinqSkipReason.RuleExpansionFailed;
                var methodName = (node.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText;
                SkippedLinqChains.Add($"Line {lineNumber}: {ex.GetType().Name} – {ex.Message}");
                Statistics.SkippedChains.Add(new LinqSkipInfo(reason, lineNumber, methodName, ex.Message));
            }
            return null;
        }

        private ExpressionSyntax TryVisitInvocationExpression(InvocationExpressionSyntax node, ForEachStatementSyntax containingForEach)
        {

            var memberAccess = node.Expression as MemberAccessExpressionSyntax;
            if (memberAccess != null)
            {
                var symbol = semantic.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;
                // Find the enclosing member: method, local function, operator, property getter,
                // constructor, or expression-bodied property.  Extracted helpers are named after
                // this owner.  BaseMethodDeclarationSyntax covers MethodDeclarationSyntax,
                // OperatorDeclarationSyntax, ConstructorDeclarationSyntax, etc.
                var owner = node.AncestorsAndSelf().FirstOrDefault(x =>
                    x is BaseMethodDeclarationSyntax || x is LocalFunctionStatementSyntax
                    || x is AccessorDeclarationSyntax || x is PropertyDeclarationSyntax);
                if (owner == null) return null;
                currentMethodIsStatic = false;
                currentMethodName = "Procedure";
                currentMethodTypeParameters = null;
                currentMethodConstraintClauses = default;
                if (owner is MethodDeclarationSyntax methodOwner)
                {
                    currentMethodIsStatic = semantic.GetDeclaredSymbol(methodOwner)?.IsStatic ?? false;
                    currentMethodName = methodOwner.Identifier.ValueText;
                    currentMethodTypeParameters = methodOwner.TypeParameterList;
                    currentMethodConstraintClauses = methodOwner.ConstraintClauses;
                }
                else if (owner is LocalFunctionStatementSyntax localFunc)
                {
                    currentMethodIsStatic = localFunc.Modifiers.Any(SyntaxKind.StaticKeyword);
                    currentMethodName = localFunc.Identifier.ValueText;
                    currentMethodTypeParameters = localFunc.TypeParameterList;
                    currentMethodConstraintClauses = localFunc.ConstraintClauses;
                }
                else if (owner is OperatorDeclarationSyntax opDecl)
                {
                    currentMethodIsStatic = semantic.GetDeclaredSymbol(opDecl)?.IsStatic ?? false;
                    // Map operator tokens to valid C# identifier segments.
                    currentMethodName = "op_" + opDecl.OperatorToken.Text switch
                    {
                        "*" => "Multiply", "/" => "Divide", "+" => "Plus", "-" => "Minus",
                        "%" => "Modulo", "==" => "Equals", "!=" => "NotEquals",
                        "<" => "LessThan", ">" => "GreaterThan",
                        "<=" => "LessThanOrEqual", ">=" => "GreaterThanOrEqual",
                        "&" => "BitwiseAnd", "|" => "BitwiseOr", "^" => "Xor",
                        "<<" => "LeftShift", ">>" => "RightShift",
                        var t => t
                    };
                    currentMethodTypeParameters = null;
                    currentMethodConstraintClauses = default;
                }
                else if (owner is ConstructorDeclarationSyntax ctorDecl)
                {
                    currentMethodIsStatic = false;
                    currentMethodName = ctorDecl.Identifier.Text;
                }
                else if (owner is AccessorDeclarationSyntax accessor)
                {
                    var prop = accessor.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                    currentMethodName = prop?.Identifier.Text ?? "Property";
                }
                else if (owner is PropertyDeclarationSyntax propDecl)
                {
                    currentMethodName = propDecl.Identifier.Text;
                }

          
                if (IsSupportedMethod(node))
                {
                    var chain = new List<LinqStep>();
                    chain.Add(new LinqStep(GetMethodFullName(node), node.ArgumentList.Arguments.Select(x => x.Expression).ToList(), node));
                    var c = node;
                    var lastNode = node;
                    while (c.Expression is MemberAccessExpressionSyntax)
                    {
                        // Strip parentheses: desugared query expressions may be wrapped in
                        // parentheses like (values.Where(x => x > 0)).Any(), so we need to
                        // look through them to chain LINQ methods correctly.
                        ExpressionSyntax receiverExpr = ((MemberAccessExpressionSyntax)c.Expression).Expression;
                        while (receiverExpr is ParenthesizedExpressionSyntax paren) receiverExpr = paren.Expression;
                        c = receiverExpr as InvocationExpressionSyntax;
                        if (c != null && IsSupportedMethod(c))
                        {
                            chain.Add(new LinqStep(GetMethodFullName(c), c.ArgumentList.Arguments.Select(x => x.Expression).ToList(), c));
                            lastNode = c;
                        }
                        else break;
                    }

                    // Record all operators encountered in this chain
                    var chainLineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    foreach (var step in chain)
                    {
                        if (step.MethodName != null && step.MethodName != IEnumerableForEachMethod)
                            Statistics.EncounteredOperators.Add(new LinqOperatorOccurrence(step.MethodName, chainLineNumber, false));
                    }

                    if (containingForEach != null)
                    {
                        chain.Insert(0, new LinqStep(IEnumerableForEachMethod, new[] { SyntaxFactory.SimpleLambdaExpression(SyntaxFactory.Parameter(containingForEach.Identifier), containingForEach.Statement) })
                        {
                            Lambda = new Lambda(containingForEach.Statement, new[] { CreateParameter(containingForEach.Identifier, semantic.GetTypeInfo(containingForEach.Type).ConvertedType) })
                        });
                    }
                    // Require at least one lambda argument OR a non-lambda intermediate
                    // (Skip, Take, Distinct) OR a terminal that benefits from procedural rewriting
                    // without lambdas (SequenceEqual).
                    if (!chain.Any(x => x.Arguments.Any(y => y is AnonymousFunctionExpressionSyntax))
                        && !chain.Any(x => x.MethodName == SkipMethod || x.MethodName == TakeMethod || x.MethodName == DistinctMethod
                            || x.MethodName == SkipLastMethod || x.MethodName == TakeLastMethod
                            || x.MethodName == AppendMethod || x.MethodName == PrependMethod
                            || x.MethodName == DefaultIfEmptyMethod || x.MethodName == DefaultIfEmptyWithValueMethod
                            || x.MethodName == ChunkMethod
                            || x.MethodName == OrderMethod || x.MethodName == OrderDescendingMethod
                            || x.MethodName == CountMethod || x.MethodName == LongCountMethod)
                        && !chain.Any(x => x.MethodName == SequenceEqualMethod
                            || x.MethodName == UnionByMethod || x.MethodName == IntersectByMethod || x.MethodName == ExceptByMethod
                            || x.MethodName == MinByMethod || x.MethodName == MaxByMethod
                            || x.MethodName == JoinMethod || x.MethodName == GroupJoinMethod))
                    {
                        Statistics.SkippedChains.Add(new LinqSkipInfo(
                            LinqSkipReason.NoLambdaOrRecognizedOperator, chainLineNumber,
                            memberAccess.Name.Identifier.ValueText,
                            "Chain has no lambda and no recognized non-lambda operator"));
                        return null;
                    }

                    var flowsIn = new List<ISymbol>();
                    var flowsOut = new List<ISymbol>();
                    foreach (var item in chain)
                    {
                        foreach (var arg in item.Arguments)
                        {
                            if (item.Lambda != null)
                            {
                                var dataFlow = semantic.AnalyzeDataFlow(item.Lambda.Body);
                                var lambdaParamNames = new HashSet<string>(
                                    item.Lambda.Parameters.Select(p => p.Identifier.ValueText));
                                foreach (var k in dataFlow.DataFlowsIn)
                                {
                                    if (lambdaParamNames.Contains(k.Name)) continue;
                                    if (!flowsIn.Contains(k)) flowsIn.Add(k);
                                }
                                foreach (var k in dataFlow.DataFlowsOut)
                                {
                                    if (lambdaParamNames.Contains(k.Name)) continue;
                                    if (!flowsOut.Contains(k)) flowsOut.Add(k);
                                }
                            }
                            else if (arg is AnonymousFunctionExpressionSyntax lambdaArg)
                            {
                                // Analyze the lambda BODY (not the whole expression) so that
                                // variables written via 'out' inside the body are visible in
                                // DataFlowsOut, and all captured reads appear in DataFlowsIn.
                                var lambdaObj = new Lambda(lambdaArg);
                                var dataFlow = semantic.AnalyzeDataFlow(lambdaObj.Body);
                                var lambdaParamNames = new HashSet<string>(
                                    lambdaObj.Parameters.Select(p => p.Identifier.ValueText));
                                foreach (var k in dataFlow.DataFlowsIn)
                                {
                                    if (lambdaParamNames.Contains(k.Name)) continue;
                                    if (!flowsIn.Contains(k)) flowsIn.Add(k);
                                }
                                foreach (var k in dataFlow.DataFlowsOut)
                                {
                                    if (lambdaParamNames.Contains(k.Name)) continue;
                                    if (!flowsOut.Contains(k)) flowsOut.Add(k);
                                }
                                // Also capture outer variables that are only written inside the
                                // lambda (e.g. "out t" where t is never read after the chain).
                                // Without this, the variable won't exist in the extracted method scope.
                                if (dataFlow.Succeeded)
                                {
                                    foreach (var k in dataFlow.WrittenInside)
                                    {
                                        if (dataFlow.VariablesDeclared.Contains(k)) continue;
                                        if (lambdaParamNames.Contains(k.Name)) continue;
                                        if (!flowsIn.Contains(k) && !flowsOut.Contains(k))
                                            flowsOut.Add(k);
                                    }
                                }
                            }
                            else
                            {
                                var dataFlow = semantic.AnalyzeDataFlow(arg);
                                foreach (var k in dataFlow.DataFlowsIn)
                                {
                                    if (!flowsIn.Contains(k)) flowsIn.Add(k);
                                }
                                foreach (var k in dataFlow.DataFlowsOut)
                                {
                                    if (!flowsOut.Contains(k)) flowsOut.Add(k);
                                }
                            }
                        }
                    }

                    // Also analyze the collection expression for captured variables
                    // (e.g. cluster.Nodes — cluster must be captured too).
                    {
                        var collectionExpr = ((MemberAccessExpressionSyntax)lastNode.Expression).Expression;
                        while (collectionExpr is ParenthesizedExpressionSyntax paren)
                            collectionExpr = paren.Expression;
                        var collectionDataFlow = semantic.AnalyzeDataFlow(collectionExpr);
                        if (collectionDataFlow.Succeeded)
                        {
                            foreach (var k in collectionDataFlow.DataFlowsIn)
                            {
                                if ((k as IParameterSymbol)?.IsThis == true) continue;
                                if (!flowsIn.Contains(k)) flowsIn.Add(k);
                            }
                        }
                    }

                    currentFlow = flowsIn
                        .Union(flowsOut)
                        .Where(x => (x as IParameterSymbol)?.IsThis != true)
                        .Select(x => CreateVariableCapture(x, flowsOut)) ?? Enumerable.Empty<VariableCapture>();

                    var collection = ((MemberAccessExpressionSyntax)lastNode.Expression).Expression;
                    // Strip parentheses from the collection expression (may appear after
                    // query desugaring: e.g. (values.Where(…)).Select(…)).
                    while (collection is ParenthesizedExpressionSyntax collectionParen)
                        collection = collectionParen.Expression;

                    if (!CanUseRecordsForAnonymousTypes && IsAnonymousType(semantic.GetTypeInfo(collection).Type))
                    {
                        Statistics.SkippedChains.Add(new LinqSkipInfo(
                            LinqSkipReason.AnonymousTypeRequiresRecords, chainLineNumber,
                            memberAccess.Name.Identifier.ValueText,
                            "Anonymous type in collection source requires record support"));
                        return null;
                    }


                    var semanticReturnType = semantic.GetTypeInfo(node).Type;
                    if (semanticReturnType == null)
                    {
                        Statistics.SkippedChains.Add(new LinqSkipInfo(
                            LinqSkipReason.ReturnTypeUnresolved, chainLineNumber,
                            memberAccess.Name.Identifier.ValueText,
                            "Return type could not be resolved"));
                        return null;
                    }
                    if (!CanUseRecordsForAnonymousTypes && (IsAnonymousType(semanticReturnType) || currentFlow.Any(x => IsAnonymousType(GetSymbolType(x.Symbol)))))
                    {
                        Statistics.SkippedChains.Add(new LinqSkipInfo(
                            LinqSkipReason.AnonymousTypeRequiresRecords, chainLineNumber,
                            memberAccess.Name.Identifier.ValueText,
                            "Anonymous type in result or captured variables requires record support"));
                        return null;
                    }


                    var result = TryRewrite(chain.First().MethodName, collection, semanticReturnType, chain, node)
                        .WithLeadingTrivia(((CSharpSyntaxNode)containingForEach ?? node).GetLeadingTrivia())
                        .WithTrailingTrivia(((CSharpSyntaxNode)containingForEach ?? node).GetTrailingTrivia());

                    // Mark all operators in this chain as successfully rewritten
                    for (int i = Statistics.EncounteredOperators.Count - 1; i >= 0; i--)
                    {
                        var op = Statistics.EncounteredOperators[i];
                        if (op.LineNumber == chainLineNumber && !op.WasRewritten)
                            Statistics.EncounteredOperators[i] = op with { WasRewritten = true };
                        else if (op.LineNumber != chainLineNumber)
                            break;
                    }

                    return result;

                }
                else
                {
                    // Track unsupported method if it looks like a LINQ operator
                    var methodFullName = GetMethodFullName(node);
                    if (methodFullName != null && methodFullName.StartsWith("System."))
                    {
                        var lineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        Statistics.EncounteredOperators.Add(new LinqOperatorOccurrence(methodFullName, lineNumber, false));
                    }
                }
            }
            return null;
        }

        private VariableCapture CreateVariableCapture(ISymbol symbol, IReadOnlyList<ISymbol> flowsOut)
        {
            var changes = flowsOut.Contains(symbol);
            if (!changes)
            {
                var local = symbol as ILocalSymbol;
                if (local != null)
                {
                    var type = local.Type;
                    if (type.IsValueType)
                    {
                        // Pass big structs by ref for performance.
                        var size = GetStructSize(type);
                        if (size > MaximumSizeForByValStruct)
                            changes = true;
                    }
                }
            }
            return new LinqRewrite.LinqRewriter.VariableCapture(symbol, changes);
        }

        private const int MaximumSizeForByValStruct = 128 / 8; // eg. two longs, or two references

        private Dictionary<ITypeSymbol, int> structSizeCache = new Dictionary<ITypeSymbol, int>();

        private int GetStructSize(ITypeSymbol type)
        {
            
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean: return 4;
                case SpecialType.System_Char: return 2;
                case SpecialType.System_SByte: return 1;
                case SpecialType.System_Byte: return 1;
                case SpecialType.System_Int16: return 2;
                case SpecialType.System_UInt16: return 2;
                case SpecialType.System_Int32: return 4;
                case SpecialType.System_UInt32: return 4;
                case SpecialType.System_Int64: return 8;
                case SpecialType.System_UInt64: return 8;
                case SpecialType.System_Single: return 4;
                case SpecialType.System_Double: return 8;
                case SpecialType.System_IntPtr: return 8;
                case SpecialType.System_UIntPtr: return 8;
                default: break;
            }
            int size;
            if (structSizeCache.TryGetValue(type, out size)) return size;


            size = 0;
            foreach (var item in type.GetMembers())
            {
                if (item.Kind == SymbolKind.Field && !item.IsStatic)
                {
                    var field = (IFieldSymbol)item;
                    if (field.Type.IsValueType)
                    {
                        if (field.Type == type)
                        {
                            // This is a primitive-like type, it "contains" itself.
                            // An unknown one, since we already ruled out some well know ones above.
                            size = 8;
                            break;
                        }
                        size += GetStructSize(field.Type);
                    }
                    else
                    {
                        size += 64 / 8;
                    }
                }
            }
            structSizeCache[type] = size;
            return size;

        }

        private bool IsSupportedMethod(InvocationExpressionSyntax invocation)
        {
            var name = GetMethodFullName(invocation);
            if (!IsSupportedMethod(name)) return false;
            if (invocation.ArgumentList.Arguments.Count != 0)
            {
                // Methods that take non-lambda arguments
                if (name == ElementAtMethod || name == ElementAtOrDefaultMethod || name == ContainsMethod
                    || name == SkipMethod || name == TakeMethod
                    || name == ConcatMethod || name == UnionMethod || name == IntersectMethod || name == ExceptMethod
                    || name == AggregateWithSeedMethod || name == SequenceEqualMethod
                    || name == ZipMethod
                    || name == SkipLastMethod || name == TakeLastMethod
                    || name == AppendMethod || name == PrependMethod || name == DefaultIfEmptyWithValueMethod
                    || name == UnionByMethod || name == IntersectByMethod || name == ExceptByMethod
                    || name == ChunkMethod
                    || name == JoinMethod || name == GroupJoinMethod
                    || name == FirstOrDefaultWithDefaultMethod || name == FirstOrDefaultWithConditionAndDefaultMethod
                    || name == LastOrDefaultWithDefaultMethod || name == LastOrDefaultWithConditionAndDefaultMethod
                    || name == SingleOrDefaultWithDefaultMethod || name == SingleOrDefaultWithConditionAndDefaultMethod)
                {
                    // These accept non-lambda args, allow them
                }
                else
                {
                    // Passing things like .Select(Method) is not supported.
                    if (invocation.ArgumentList.Arguments.Any(x => !(x.Expression is AnonymousFunctionExpressionSyntax)))
                        return false;
                }
            }
            return true;
        }

        private bool IsSupportedMethod(string v)
        {
            if (v == null) return false;
            if (KnownMethods.Contains(v)) return true;
            if (!v.StartsWith("System.Collections.Generic.IEnumerable<")) return false;
            var k = v.Replace("<", "(");
            if (!k.Contains(">.Sum(") && !k.Contains(">.Average(") && !k.Contains(">.Min(") && !k.Contains(">.Max(")) return false;
            if (k.Contains("TResult")) return false;
            if (v == "System.Collections.Generic.IEnumerable<TSource>.Min()") return false;
            if (v == "System.Collections.Generic.IEnumerable<TSource>.Max()") return false;
            return true;
        }

        private StatementSyntax CreateStatement(ExpressionSyntax expression)
        {
            return SyntaxFactory.ExpressionStatement(expression);
        }

        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (HasNoRewriteAttribute(node.AttributeLists)) return node;
            var old = RewrittenLinqQueries;
            var k = base.VisitMethodDeclaration(node);
            if (RewrittenLinqQueries != old)
            {
                RewrittenMethods++;
                Statistics.RewrittenMethodCount++;
            }
            return k;
        }

        private bool HasNoRewriteAttribute(SyntaxList<AttributeListSyntax> attributeLists)
        {
            return attributeLists.Any(x => x.Attributes.Any(y =>
            {
                var symbolInfo = semantic.GetSymbolInfo(y);
                if (symbolInfo.Symbol == null)
                    return false;

                var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
                if (methodSymbol == null)
                    return false;

                var containingType = methodSymbol.ContainingType;
                if (containingType == null)
                    return false;

                return containingType.ToDisplayString() == "Shaman.Runtime.NoLinqRewriteAttribute";
            }));
        }

        private bool IsAnonymousType(ITypeSymbol t)
        {
            return (t.ToDisplayString().Contains("anonymous type:"));
        }

        /// <summary>
        /// Returns a valid C# type name string for <paramref name="type"/>,
        /// replacing any anonymous type components with <c>object</c> so that
        /// <see cref="SyntaxFactory.ParseTypeName"/> can produce a valid TypeSyntax.
        /// </summary>
        private string SanitizeAnonymousTypeDisplay(ITypeSymbol type)
        {
            if (IsAnonymousType(type))
                return "object";

            // Type parameters leaked from LINQ method definitions that don't belong
            // to the enclosing method or type should be replaced with object.
            if (type is ITypeParameterSymbol tp && !IsDeclaredTypeParameter(tp))
                return "object";

            if (type is INamedTypeSymbol named && named.IsGenericType
                && named.TypeArguments.Any(IsAnonymousType))
            {
                // Reconstruct with 'object' in place of anonymous type args.
                var baseName = named.OriginalDefinition.ToDisplayString();
                var idx = baseName.IndexOf('<');
                if (idx >= 0) baseName = baseName.Substring(0, idx);
                var args = string.Join(", ", named.TypeArguments.Select(a =>
                    IsAnonymousType(a) ? "object" : a.ToDisplayString()));
                return $"{baseName}<{args}>";
            }

            return type.ToDisplayString();
        }

        private ThrowStatementSyntax CreateThrowException(string type, string message = null)
        {
            return SyntaxFactory.ThrowStatement(SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(type), CreateArguments(message!=null? new[] { SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(message)) } :  new ExpressionSyntax[] { }), null));
        }

        private static ParameterSyntax GetLambdaParameter(Lambda lambda, int index)
        {
            return lambda.Parameters[index];
        }

        private ITypeSymbol GetLambdaReturnType(AnonymousFunctionExpressionSyntax lambda)
        {
            var symbol = ((INamedTypeSymbol)semantic.GetTypeInfo(lambda).ConvertedType).TypeArguments.Last();
            return symbol;
        }



        private Lambda RenameSymbol(Lambda container, int argIndex, string newname)
        {
            var oldparameter = GetLambdaParameter(container, argIndex).Identifier.ValueText;
            //var oldsymbol = semantic.GetDeclaredSymbol(oldparameter);
            var tokensToRename = container.Body.DescendantNodesAndSelf().Where(x =>
            {
                var sem = semantic.GetSymbolInfo(x).Symbol;
                if (sem != null && (sem is ILocalSymbol || sem is IParameterSymbol) && sem.Name == oldparameter) return true;
                //  if (sem.Symbol == oldsymbol) return true;
                return false;
            });
            var syntax = SyntaxFactory.ParenthesizedLambdaExpression(CreateParameters(container.Parameters.Select((x, i) => i == argIndex ? SyntaxFactory.Parameter(SyntaxFactory.Identifier(newname)).WithType(x.Type) : x)), container.Body.ReplaceNodes(tokensToRename, (a, b) =>
                 {
                     var ide = b as IdentifierNameSyntax;
                     if (ide != null) return ide.WithIdentifier(SyntaxFactory.Identifier(newname));
                     throw new NotImplementedException();
                 }));
            return new Lambda(syntax);
            //var doc = project.GetDocument(docid);

            //var annot = new SyntaxAnnotation("RenamedLambda");
            //var annotated = container.WithAdditionalAnnotations(annot);
            //var root = project.GetDocument(docid).GetSyntaxRootAsync().Result.ReplaceNode(container, annotated).SyntaxTree;
            //var proj = project.GetDocument(docid).WithSyntaxRoot(root.GetRoot()).Project;
            //doc = proj.GetDocument(docid);
            //var syntaxTree = doc.GetSyntaxTreeAsync().Result;
            //var modifiedSemantic = proj.GetCompilationAsync().Result.GetSemanticModel(syntaxTree);
            //annotated = (AnonymousFunctionExpressionSyntax)doc.GetSyntaxRootAsync().Result.GetAnnotatedNodes(annot).First();
            //var parameter = GetLambdaParameter(annotated, 0);
            //var renamed = Renamer.RenameSymbolAsync(proj.Solution, modifiedSemantic.GetDeclaredSymbol(parameter), newname, null).Result;
            //annotated = (AnonymousFunctionExpressionSyntax)renamed.GetDocument(doc.Id).GetSyntaxRootAsync().Result.GetAnnotatedNodes(annot).First();
            //return annotated.WithoutAnnotations();
        }



        ITypeSymbol GetSymbolType(VariableCapture x)
        {
            return GetSymbolType(x.Symbol);
        }

        private string GetMethodFullName(InvocationExpressionSyntax invocation)
        {
            var n = (semantic.GetSymbolInfo(invocation.Expression).Symbol as IMethodSymbol)?.OriginalDefinition.ToDisplayString();

            // Fallback: when semantic model can't resolve the LINQ method (common in
            // project pipeline), derive the method identity from syntax for known methods.
            if (n == null && invocation.Expression is MemberAccessExpressionSyntax ma)
            {
                var methodName = ma.Name.Identifier.Text;
                n = methodName switch
                {
                    "Count" or "LongCount" => $"System.Collections.Generic.IEnumerable<TSource>.{methodName}<TSource>()",
                    "Any" => $"System.Collections.Generic.IEnumerable<TSource>.Any<TSource>()",
                    "All" => $"System.Collections.Generic.IEnumerable<TSource>.All<TSource>(System.Func<TSource, bool>)",
                    "First" => $"System.Collections.Generic.IEnumerable<TSource>.First<TSource>()",
                    "FirstOrDefault" => $"System.Collections.Generic.IEnumerable<TSource>.FirstOrDefault<TSource>()",
                    "Last" => $"System.Collections.Generic.IEnumerable<TSource>.Last<TSource>()",
                    "LastOrDefault" => $"System.Collections.Generic.IEnumerable<TSource>.LastOrDefault<TSource>()",
                    "Single" => $"System.Collections.Generic.IEnumerable<TSource>.Single<TSource>()",
                    "SingleOrDefault" => $"System.Collections.Generic.IEnumerable<TSource>.SingleOrDefault<TSource>()",
                    "ElementAt" => $"System.Collections.Generic.IEnumerable<TSource>.ElementAt<TSource>(int)",
                    "ElementAtOrDefault" => $"System.Collections.Generic.IEnumerable<TSource>.ElementAtOrDefault<TSource>(int)",
                    "Where" => $"System.Collections.Generic.IEnumerable<TSource>.Where<TSource>(System.Func<TSource, bool>)",
                    "Select" => $"System.Collections.Generic.IEnumerable<TSource>.Select<TSource, TResult>(System.Func<TSource, TResult>)",
                    "SelectMany" => $"System.Collections.Generic.IEnumerable<TSource>.SelectMany<TSource, TResult>(System.Func<TSource, System.Collections.Generic.IEnumerable<TResult>>)",
                    "OrderBy" => $"System.Collections.Generic.IEnumerable<TSource>.OrderBy<TSource, TKey>(System.Func<TSource, TKey>)",
                    "OrderByDescending" => $"System.Collections.Generic.IEnumerable<TSource>.OrderByDescending<TSource, TKey>(System.Func<TSource, TKey>)",
                    _ => null
                };
            }

            const string ienumerableOfTsource = "System.Collections.Generic.IEnumerable<TSource>";
            if (n != null)
            {
                // Normalize any collection type (List, ICollection, IList, ISet, etc.)
                // to IEnumerable<TSource> so the method identity matches KnownMethods.
                n = System.Text.RegularExpressions.Regex.Replace(
                    n,
                    @"^System\.Collections\.Generic\.\w+<TSource>\.",
                    ienumerableOfTsource + ".");
                n = n.Replace("TSource[]", ienumerableOfTsource);
            }

            return n;
        }

        const string ItemsName = "_linqitems";
        const string ItemName = "_linqitem";
        private class VariableCapture

        {
            public VariableCapture(ISymbol symbol, bool changes)
            {
                this.Symbol = symbol;
                this.Changes = changes;
            }
            public ISymbol Symbol { get; }
            public bool Changes { get; }
            public string Name
            {
                get { return Symbol.Name; }
            }
        }

        delegate StatementSyntax AggregationDelegate(LinqStep invocation, ArgumentListSyntax arguments, ParameterSyntax param);
        private AggregationDelegate currentAggregation;

        /// <summary>
        /// Scans the chain for intermediate operators that need prologue variables
        /// (e.g., Distinct needs a HashSet, Skip needs a counter, etc.)
        /// </summary>
        private IEnumerable<StatementSyntax> GetIntermediatePrologue(List<LinqStep> chain)
        {
            var result = new List<StatementSyntax>();
            for (int i = 0; i < chain.Count; i++)
            {
                var step = chain[i];
                if (step.MethodName == DistinctMethod)
                {
                    result.Add(CreateLocalVariableDeclaration("_seen",
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.HashSet<" + GetIntermediateItemTypeForStep(step) + ">"),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)));
                }
                else if (step.MethodName == DistinctByMethod)
                {
                    var methodSymbol = semantic.GetSymbolInfo(step.Invocation).Symbol as IMethodSymbol;
                    var keyType = methodSymbol.TypeArguments[1]; // TKey
                    result.Add(CreateLocalVariableDeclaration("_seenKeys_" + i,
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.HashSet<" + keyType.ToDisplayString() + ">"),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)));
                }
                else if (step.MethodName == SkipMethod)
                {
                    result.Add(CreateLocalVariableDeclaration("_skipCount", SyntaxFactory.IdentifierName("_skipCount_param")));
                }
                else if (step.MethodName == TakeMethod)
                {
                    result.Add(CreateLocalVariableDeclaration("_takeCount", SyntaxFactory.IdentifierName("_takeCount_param")));
                }
                else if (step.MethodName == SkipWhileMethod)
                {
                    result.Add(CreateLocalVariableDeclaration("_skipWhileActive",
                        SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)));
                }
                else if (step.MethodName == SkipWhileWithIndexMethod)
                {
                    result.Add(CreateLocalVariableDeclaration("_skipWhileActive_" + i,
                        SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)));
                    result.Add(CreateLocalVariableDeclaration("_idxCounter_" + i,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))));
                }
                else if (step.MethodName == WhereWithIndexMethod
                      || step.MethodName == SelectWithIndexMethod
                      || step.MethodName == TakeWhileWithIndexMethod
                      || step.MethodName == SelectManyWithIndexMethod)
                {
                    result.Add(CreateLocalVariableDeclaration("_idxCounter_" + i,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))));
                }
                else if (step.MethodName == ZipMethod)
                {
                    var zipMethodSymbol = semantic.GetSymbolInfo(step.Invocation).Symbol as IMethodSymbol;
                    var zipSecondItemType = GetItemType(zipMethodSymbol.Parameters[0].Type);
                    result.Add(CreateLocalVariableDeclaration("_zipList",
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + zipSecondItemType.ToDisplayString() + ">"),
                            CreateArguments(new ExpressionSyntax[] { SyntaxFactory.IdentifierName("_zipSecond") }),
                            null)));
                    result.Add(CreateLocalVariableDeclaration("_zipIndex",
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))));
                }
                else if (step.MethodName == SkipLastMethod)
                {
                    // Ring buffer: Queue<T> to delay output by N elements
                    result.Add(CreateLocalVariableDeclaration("_skipLastBuffer_" + i,
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.Queue<" + GetIntermediateItemTypeForStep(step) + ">"),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)));
                    result.Add(CreateLocalVariableDeclaration("_skipLastN_" + i,
                        SyntaxFactory.IdentifierName("_skipLastN_param_" + i)));
                }
                else if (step.MethodName == TakeLastMethod)
                {
                    // Ring buffer: Queue<T> to keep last N elements
                    result.Add(CreateLocalVariableDeclaration("_takeLastBuffer_" + i,
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.Queue<" + GetIntermediateItemTypeForStep(step) + ">"),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)));
                    result.Add(CreateLocalVariableDeclaration("_takeLastN_" + i,
                        SyntaxFactory.IdentifierName("_takeLastN_param_" + i)));
                }
                else if (step.MethodName == DefaultIfEmptyMethod || step.MethodName == DefaultIfEmptyWithValueMethod)
                {
                    result.Add(CreateLocalVariableDeclaration("_hasElements_" + i,
                        SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)));
                }
                else if (step.MethodName == ChunkMethod)
                {
                    // Buffer for individual items (TSource, not TSource[])
                    var methodSymbol = semantic.GetSymbolInfo(step.Invocation).Symbol as IMethodSymbol;
                    var elementType = methodSymbol.TypeArguments[0]; // TSource
                    result.Add(CreateLocalVariableDeclaration("_chunkBuffer_" + i,
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + elementType.ToDisplayString() + ">"),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)));
                    result.Add(CreateLocalVariableDeclaration("_chunkSize_" + i,
                        SyntaxFactory.IdentifierName("_chunkSize_param_" + i)));
                }
                else if (step.MethodName == OrderMethod || step.MethodName == OrderDescendingMethod)
                {
                    result.Add(CreateLocalVariableDeclaration("_sortBuffer_" + i,
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + GetIntermediateItemTypeForStep(step) + ">"),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)));
                }
            }
            return result;
        }

        private string GetIntermediateItemTypeForStep(LinqStep step)
        {
            // Use the semantic model to get the item type from the invocation
            if (step.Invocation != null)
            {
                var typeInfo = semantic.GetTypeInfo(step.Invocation);
                var itemType = GetItemType(typeInfo.Type);
                if (itemType != null) return itemType.ToDisplayString();
            }
            return "object";
        }

        /// <summary>
        /// Generates statements to execute before the main foreach loop.
        /// Used for Prepend (emit prepended element through inner chain).
        /// </summary>
        private IEnumerable<StatementSyntax> GetIntermediatePreLoopStatements(List<LinqStep> chain, TypeSyntax collectionItemType, string itemName, ArgumentListSyntax arguments, bool noAggregation)
        {
            var result = new List<StatementSyntax>();
            for (int i = 0; i < chain.Count; i++)
            {
                var step = chain[i];
                if (step.MethodName == PrependMethod)
                {
                    var prependItemName = "_prependItem" + (++lastId);
                    var inner = CreateProcessingStep(chain, i - 1, collectionItemType, prependItemName, arguments, noAggregation);
                    result.Add(SyntaxFactory.Block(
                        CreateLocalVariableDeclaration(prependItemName, SyntaxFactory.IdentifierName("_prependValue_" + i)),
                        inner));
                }
            }
            return result;
        }

        /// <summary>
        /// Generates statements to execute after the main foreach loop.
        /// Used for Append, TakeLast, DefaultIfEmpty.
        /// </summary>
        private IEnumerable<StatementSyntax> GetIntermediatePostLoopStatements(List<LinqStep> chain, TypeSyntax collectionItemType, string itemName, ArgumentListSyntax arguments, bool noAggregation)
        {
            var result = new List<StatementSyntax>();
            for (int i = 0; i < chain.Count; i++)
            {
                var step = chain[i];
                if (step.MethodName == AppendMethod)
                {
                    var appendItemName = "_appendItem" + (++lastId);
                    var inner = CreateProcessingStep(chain, i - 1, collectionItemType, appendItemName, arguments, noAggregation);
                    result.Add(SyntaxFactory.Block(
                        CreateLocalVariableDeclaration(appendItemName, SyntaxFactory.IdentifierName("_appendValue_" + i)),
                        inner));
                }
                else if (step.MethodName == TakeLastMethod)
                {
                    var bufferItemName = "_takeLastItem" + (++lastId);
                    var inner = CreateProcessingStep(chain, i - 1, collectionItemType, bufferItemName, arguments, noAggregation);
                    result.Add(SyntaxFactory.ForEachStatement(
                        SyntaxFactory.ParseTypeName("var"),
                        bufferItemName,
                        SyntaxFactory.IdentifierName("_takeLastBuffer_" + i),
                        inner is BlockSyntax ? inner : SyntaxFactory.Block(inner)));
                }
                else if (step.MethodName == DefaultIfEmptyMethod)
                {
                    var defaultItemName = "_defaultItem" + (++lastId);
                    var inner = CreateProcessingStep(chain, i - 1, collectionItemType, defaultItemName, arguments, noAggregation);
                    result.Add(SyntaxFactory.IfStatement(
                        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                            SyntaxFactory.IdentifierName("_hasElements_" + i)),
                        SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(defaultItemName,
                                SyntaxFactory.DefaultExpression(collectionItemType)),
                            inner)));
                }
                else if (step.MethodName == DefaultIfEmptyWithValueMethod)
                {
                    var defaultItemName = "_defaultItem" + (++lastId);
                    var inner = CreateProcessingStep(chain, i - 1, collectionItemType, defaultItemName, arguments, noAggregation);
                    result.Add(SyntaxFactory.IfStatement(
                        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                            SyntaxFactory.IdentifierName("_hasElements_" + i)),
                        SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(defaultItemName,
                                SyntaxFactory.IdentifierName("_defaultValue_" + i)),
                            inner)));
                }
                else if (step.MethodName == ChunkMethod)
                {
                    // Emit remaining items in the buffer as a final chunk
                    var methodSymbol = semantic.GetSymbolInfo(step.Invocation).Symbol as IMethodSymbol;
                    var elementType = methodSymbol.TypeArguments[0]; // TSource
                    var elementTypeSyntax = SyntaxFactory.ParseTypeName(elementType.ToDisplayString());
                    var chunkItemType = SyntaxFactory.ArrayType(elementTypeSyntax, SyntaxFactory.List(new[] { SyntaxFactory.ArrayRankSpecifier() }));
                    var chunkItemName = "_chunkArrayFinal" + (++lastId);
                    var inner = CreateProcessingStep(chain, i - 1, chunkItemType, chunkItemName, arguments, noAggregation);
                    result.Add(SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(SyntaxKind.GreaterThanExpression,
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("_chunkBuffer_" + i),
                                SyntaxFactory.IdentifierName("Count")),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))),
                        SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(chunkItemName,
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName("_chunkBuffer_" + i),
                                        SyntaxFactory.IdentifierName("ToArray")))),
                            inner)));
                }
                else if (step.MethodName == OrderMethod || step.MethodName == OrderDescendingMethod)
                {
                    // Sort the buffer, then iterate sorted items through remaining chain
                    var sortedItemName = "_sortItem" + (++lastId);
                    var inner = CreateProcessingStep(chain, i - 1, collectionItemType, sortedItemName, arguments, noAggregation);

                    var statements = new List<StatementSyntax>();
                    // _sortBuffer.Sort();
                    statements.Add(SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("_sortBuffer_" + i),
                                SyntaxFactory.IdentifierName("Sort")))));
                    // For OrderDescending: _sortBuffer.Reverse();
                    if (step.MethodName == OrderDescendingMethod)
                    {
                        statements.Add(SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("_sortBuffer_" + i),
                                    SyntaxFactory.IdentifierName("Reverse")))));
                    }
                    // foreach (var _sortItem in _sortBuffer) { inner }
                    statements.Add(SyntaxFactory.ForEachStatement(
                        SyntaxFactory.ParseTypeName("var"),
                        sortedItemName,
                        SyntaxFactory.IdentifierName("_sortBuffer_" + i),
                        inner is BlockSyntax ? inner : SyntaxFactory.Block(inner)));
                    result.Add(SyntaxFactory.Block(statements));
                }
            }
            return result;
        }

        private ExpressionSyntax RewriteAsLoop(TypeSyntax returnType, IEnumerable<StatementSyntax> prologue, IEnumerable<StatementSyntax> epilogue, ExpressionSyntax collection, List<LinqStep> chain, AggregationDelegate k, bool noaggregation = false, IEnumerable<Tuple<ParameterSyntax, ExpressionSyntax>> additionalParameters = null)
        {
            var old = currentAggregation;
            currentAggregation = k;

            var collectionType = semantic.GetTypeInfo(collection).Type;
            var collectionItemType = GetItemType(collectionType);
            if (collectionItemType == null) throw new NotSupportedException();
            var collectionSemanticType = semantic.GetTypeInfo(collection).Type;

            // Indexed for-loop will be generated for List<T> and arrays
            // (must match the condition in the indexed-vs-foreach branch below).
            bool usesIndexedLoop = collectionType.ToDisplayString().StartsWith("System.Collections.Generic.List<")
                                || collectionSemanticType is IArrayTypeSymbol;

            // When indexed loop is used the parameter needs concrete type so that
            // .Count/.Length and [_index] work.  For other collections use
            // IEnumerable<T> (→ Iterable<T> in Java) to avoid concrete-type
            // conflicts (e.g. java.util.Set vs project Set).
            ITypeSymbol linqItemsParamType;
            if (usesIndexedLoop)
            {
                linqItemsParamType = collectionSemanticType;
            }
            else
            {
                var ienumerableType = semantic.Compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T);
                linqItemsParamType = ienumerableType.Construct(collectionItemType);
            }

            var parameters =  new[] { CreateParameter(ItemsName, linqItemsParamType) }.Concat(currentFlow.Select(x => CreateParameter(x.Name, GetSymbolType(x.Symbol)).WithRef(x.Changes)));
            if (additionalParameters != null) parameters = parameters.Concat(additionalParameters.Select(x => x.Item1));

            // Add parameters for intermediates that need non-lambda arguments passed in
            var intermediateParams = new List<Tuple<ParameterSyntax, ExpressionSyntax>>();
            foreach (var step in chain)
            {
                if (step.MethodName == SkipMethod && step.Arguments.Count > 0)
                {
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_skipCount_param", CreatePrimitiveType(SyntaxKind.IntKeyword)),
                        step.Arguments[0]));
                }
                else if (step.MethodName == TakeMethod && step.Arguments.Count > 0)
                {
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_takeCount_param", CreatePrimitiveType(SyntaxKind.IntKeyword)),
                        step.Arguments[0]));
                }
                else if (step.MethodName == ZipMethod && step.Arguments.Count > 0)
                {
                    var zipMethodSymbol = semantic.GetSymbolInfo(step.Invocation).Symbol as IMethodSymbol;
                    var zipSecondType = zipMethodSymbol.Parameters[0].Type;
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_zipSecond", SyntaxFactory.ParseTypeName(zipSecondType.ToDisplayString())),
                        step.Arguments[0]));
                }
                else if (step.MethodName == SkipLastMethod && step.Arguments.Count > 0)
                {
                    var idx = chain.IndexOf(step);
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_skipLastN_param_" + idx, CreatePrimitiveType(SyntaxKind.IntKeyword)),
                        step.Arguments[0]));
                }
                else if (step.MethodName == TakeLastMethod && step.Arguments.Count > 0)
                {
                    var idx = chain.IndexOf(step);
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_takeLastN_param_" + idx, CreatePrimitiveType(SyntaxKind.IntKeyword)),
                        step.Arguments[0]));
                }
                else if (step.MethodName == AppendMethod && step.Arguments.Count > 0)
                {
                    var idx = chain.IndexOf(step);
                    var methodSymbol = semantic.GetSymbolInfo(step.Invocation).Symbol as IMethodSymbol;
                    var paramType = methodSymbol.Parameters[0].Type;
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_appendValue_" + idx, SyntaxFactory.ParseTypeName(paramType.ToDisplayString())),
                        step.Arguments[0]));
                }
                else if (step.MethodName == PrependMethod && step.Arguments.Count > 0)
                {
                    var idx = chain.IndexOf(step);
                    var methodSymbol = semantic.GetSymbolInfo(step.Invocation).Symbol as IMethodSymbol;
                    var paramType = methodSymbol.Parameters[0].Type;
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_prependValue_" + idx, SyntaxFactory.ParseTypeName(paramType.ToDisplayString())),
                        step.Arguments[0]));
                }
                else if (step.MethodName == DefaultIfEmptyWithValueMethod && step.Arguments.Count > 0)
                {
                    var idx = chain.IndexOf(step);
                    var methodSymbol = semantic.GetSymbolInfo(step.Invocation).Symbol as IMethodSymbol;
                    var paramType = methodSymbol.Parameters[0].Type;
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_defaultValue_" + idx, SyntaxFactory.ParseTypeName(paramType.ToDisplayString())),
                        step.Arguments[0]));
                }
                else if (step.MethodName == ChunkMethod && step.Arguments.Count > 0)
                {
                    var idx = chain.IndexOf(step);
                    intermediateParams.Add(Tuple.Create(
                        CreateParameter("_chunkSize_param_" + idx, CreatePrimitiveType(SyntaxKind.IntKeyword)),
                        step.Arguments[0]));
                }
            }
            if (intermediateParams.Count > 0) parameters = parameters.Concat(intermediateParams.Select(x => x.Item1));

            var functionName = GetUniqueName(currentMethodName + "_ProceduralLinq");
            var arguments = CreateArguments(new[] { SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ItemName)) }.Concat(currentFlow.Select(x => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(x.Name)).WithRef(x.Changes))));

            var loopContent = CreateProcessingStep(chain, chain.Count - 1, SyntaxFactory.ParseTypeName(collectionItemType.ToDisplayString()), ItemName, arguments, noaggregation);

            // Build intermediate prologue (HashSet for Distinct, counters for Skip/Take, etc.)
            var intermediatePrologue = GetIntermediatePrologue(chain);

            // Pre/post-loop statements for Prepend, Append, TakeLast, DefaultIfEmpty
            var collectionItemTypeSyntax = SyntaxFactory.ParseTypeName(collectionItemType.ToDisplayString());
            var preLoopStatements = GetIntermediatePreLoopStatements(chain, collectionItemTypeSyntax, ItemName, arguments, noaggregation);
            var postLoopStatements = GetIntermediatePostLoopStatements(chain, collectionItemTypeSyntax, ItemName, arguments, noaggregation);

            StatementSyntax foreachStatement;
            if (collectionType.ToDisplayString().StartsWith("System.Collections.Generic.List<") || collectionType is IArrayTypeSymbol)
            {

                foreachStatement = SyntaxFactory.ForStatement(
                    SyntaxFactory.VariableDeclaration(CreatePrimitiveType(SyntaxKind.IntKeyword), CreateSeparatedList<VariableDeclaratorSyntax>(SyntaxFactory.VariableDeclarator("_index").WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0)))))), default(SeparatedSyntaxList<ExpressionSyntax>),
                    SyntaxFactory.BinaryExpression(SyntaxKind.LessThanExpression, SyntaxFactory.IdentifierName("_index"), GetCollectionCount(collection, false)), CreateSeparatedList<ExpressionSyntax>(SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, SyntaxFactory.IdentifierName("_index"))),
                    SyntaxFactory.Block(new StatementSyntax[] { CreateLocalVariableDeclaration(ItemName, SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName(ItemsName), SyntaxFactory.BracketedArgumentList(CreateSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_index")))))) }.Union((loopContent as BlockSyntax)?.Statements ?? (IEnumerable<StatementSyntax>)new[] { loopContent })));
            }
            else
            {
                foreachStatement = SyntaxFactory.ForEachStatement(
                SyntaxFactory.IdentifierName("var"),
                ItemName,
                SyntaxFactory.IdentifierName(ItemsName),
                loopContent is BlockSyntax ? loopContent : SyntaxFactory.Block(loopContent));

            }



            var coreFunction = SyntaxFactory.MethodDeclaration(returnType, functionName)
                        .WithParameterList(CreateParameters(parameters))
                        .WithBody(SyntaxFactory.Block((collectionSemanticType.IsValueType ? Enumerable.Empty<StatementSyntax>() : new[] {
                            SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, SyntaxFactory.IdentifierName(ItemsName) ,SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)), CreateThrowException("System.ArgumentNullException"))
                        }).Concat(prologue).Concat(intermediatePrologue).Concat(preLoopStatements).Concat(new[] {
                            foreachStatement
                        }).Concat(postLoopStatements).Concat(epilogue)))
                        .WithStatic(currentMethodIsStatic)
                        .WithTypeParameterList(currentMethodTypeParameters)
                        .WithConstraintClauses(currentMethodConstraintClauses)
                        .NormalizeWhitespace();
            methodsToAddToCurrentType.Add(Tuple.Create(currentType, coreFunction));

            // Java Map doesn't implement Iterable<Map.Entry> — need .entrySet()
            // at the call site when a Dictionary is passed to Iterable<Entry> param.
            var visitedCollection = (ExpressionSyntax)Visit(collection);
            if (!usesIndexedLoop && collectionType is INamedTypeSymbol _namedDictType &&
                (_namedDictType.OriginalDefinition.ToDisplayString() is
                    "System.Collections.Generic.Dictionary<TKey, TValue>" or
                    "System.Collections.Generic.IDictionary<TKey, TValue>" or
                    "System.Collections.Generic.SortedDictionary<TKey, TValue>" or
                    "System.Collections.Generic.SortedList<TKey, TValue>" or
                    "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>" or
                    "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>" ||
                 _namedDictType.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is
                    "System.Collections.Generic.IDictionary<TKey, TValue>")))
            {
                visitedCollection = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        visitedCollection,
                        SyntaxFactory.IdentifierName("entrySet")));
            }

            IEnumerable<ArgumentSyntax> args = new[] { SyntaxFactory.Argument(visitedCollection) }.Concat(arguments.Arguments.Skip(1));
            if (additionalParameters != null) args = args.Concat(additionalParameters.Select(x => SyntaxFactory.Argument(x.Item2)));
            if (intermediateParams.Count > 0) args = args.Concat(intermediateParams.Select(x => SyntaxFactory.Argument(x.Item2)));
            var inv = SyntaxFactory.InvocationExpression(GetMethodNameSyntaxWithCurrentTypeParameters(functionName), CreateArguments(args));

            currentAggregation = old;
            return inv;
        }

        private string GetUniqueName(string v)
        {
            for (int i = 1; ; i++)
            {
                var name = v + i;
                if (methodsToAddToCurrentType.Any(x => x.Item2.Identifier.ValueText == name)) continue;
                return name;
            }
        }

        private ITypeSymbol GetItemType(ITypeSymbol collectionType)
        {
            var itemType = collectionType is IArrayTypeSymbol ? ((IArrayTypeSymbol)collectionType).ElementType : collectionType.AllInterfaces.Concat(new[] { collectionType }).OfType<INamedTypeSymbol>().FirstOrDefault(x => x.IsGenericType && x.ConstructUnboundGenericType().ToString() == "System.Collections.Generic.IEnumerable<>")?.TypeArguments.First();
            if (itemType is ITypeParameterSymbol tp
                && !IsDeclaredTypeParameter(tp))
            {
                return semantic.Compilation.GetSpecialType(SpecialType.System_Object);
            }
            return itemType;
        }

        /// <summary>Returns true when <paramref name="tp"/> is declared by the enclosing
        /// method or the enclosing type — i.e. it is a legitimate generic parameter, not a
        /// leaked LINQ-method type parameter (e.g. TSource from Enumerable.Select).</summary>
        private bool IsDeclaredTypeParameter(ITypeParameterSymbol tp)
        {
            if (currentMethodTypeParameters?.Parameters.Any(
                    p => p.Identifier.ValueText == tp.Name) == true)
                return true;
            if (currentType?.TypeParameterList?.Parameters.Any(
                    p => p.Identifier.ValueText == tp.Name) == true)
                return true;
            return false;
        }

        private static PredefinedTypeSyntax CreatePrimitiveType(SyntaxKind keyword)
        {
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(keyword));
        }

        private List<Tuple<TypeDeclarationSyntax, MethodDeclarationSyntax>> methodsToAddToCurrentType = new List<Tuple<TypeDeclarationSyntax, MethodDeclarationSyntax>>();
        private int lastId;
        private bool currentMethodIsStatic;
        private string currentMethodName;
        private IEnumerable<VariableCapture> currentFlow;
        

        public override SyntaxNode VisitStructDeclaration(StructDeclarationSyntax node)
        {
            return VisitTypeDeclaration(node);
        }

        private SyntaxNode VisitTypeDeclaration(TypeDeclarationSyntax node)
        {
            if (HasNoRewriteAttribute(node.AttributeLists)) return node;

            var old = currentType;
            currentType = node;
            var changed = (TypeDeclarationSyntax)(node is ClassDeclarationSyntax ? base.VisitClassDeclaration((ClassDeclarationSyntax)node) : base.VisitStructDeclaration((StructDeclarationSyntax)node));
            if (methodsToAddToCurrentType.Count != 0)
            {
                var newmembers = methodsToAddToCurrentType.Where(x => x.Item1 == currentType).Select(x => x.Item2).ToArray();
                var withMethods = changed is ClassDeclarationSyntax ? (TypeDeclarationSyntax)((ClassDeclarationSyntax)changed).AddMembers(newmembers) : ((StructDeclarationSyntax)changed).AddMembers(newmembers);
                methodsToAddToCurrentType.RemoveAll(x => x.Item1 == currentType);
                currentType = old;
                return withMethods.NormalizeWhitespace();
            }
            currentType = old;
            return changed;
        }

        public override SyntaxNode VisitForEachStatement(ForEachStatementSyntax node)
        {
            return TryVisitForEachStatement(node) ?? base.VisitForEachStatement(node);
        }

        private SyntaxNode TryVisitForEachStatement(ForEachStatementSyntax node)
        {
            var collection = node.Expression as InvocationExpressionSyntax;
            if (collection != null && IsSupportedMethod(collection))
            {
                var visitor = new CanRewrapForeachVisitor();
                visitor.Visit(node.Statement);
                if (!visitor.Fail)
                {
                    var k = TryCatchVisitInvocationExpression(collection, node);
                    if (k != null) return SyntaxFactory.ExpressionStatement(k);
                }
            }
            return base.VisitForEachStatement(node);
        }

        private ExpressionSyntax InlineOrCreateMethod(Lambda lambda, TypeSyntax returnType, ArgumentListSyntax arguments, ParameterSyntax param)
        {
            var p = GetLambdaParameter(lambda, 0).Identifier.ValueText;
            //var lambdaParameter = semantic.GetDeclaredSymbol(p);
            var currentFlow = semantic.AnalyzeDataFlow(lambda.Body);
            var currentCaptures = currentFlow
                .DataFlowsOut
                .Union(currentFlow.DataFlowsIn)
                .Where(x => x.Name != p && (x as IParameterSymbol)?.IsThis != true)
                .Select(x => CreateVariableCapture(x, currentFlow.DataFlowsOut))
                .ToList();
            lambda = RenameSymbol(lambda, 0, param.Identifier.ValueText);


            return InlineOrCreateMethod(lambda.Body, returnType, param, currentCaptures);
        }

        private ExpressionSyntax InlineOrCreateMethod(CSharpSyntaxNode body, TypeSyntax returnType, ParameterSyntax param, IEnumerable<VariableCapture> captures)
        {

            var fn = GetUniqueName(currentMethodName + "_ProceduralLinqHelper");

            if (body is ExpressionSyntax && true)
            {
                return (ExpressionSyntax)body;
            }
            else
            {
                if (captures.Any(x => IsAnonymousType(GetSymbolType(x.Symbol)))) throw new NotSupportedException();
                if (returnType == null) throw new NotSupportedException(); // Anonymous type
                var method = SyntaxFactory.MethodDeclaration(returnType, fn)
                                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
                                    new[] {
                                    param
                                    }.Union(captures.Select(x => CreateParameter(x.Name, GetSymbolType(x)).WithRef(x.Changes)))
                                 )))
                                .WithBody(body as BlockSyntax ?? (body is StatementSyntax ? SyntaxFactory.Block((StatementSyntax)body) : SyntaxFactory.Block(SyntaxFactory.ReturnStatement((ExpressionSyntax)body))))
                                .WithStatic(currentMethodIsStatic)
                                .WithTypeParameterList(currentMethodTypeParameters)
                                .WithConstraintClauses(currentMethodConstraintClauses)
                                .NormalizeWhitespace();

                methodsToAddToCurrentType.Add(Tuple.Create(currentType, method));


                return SyntaxFactory.InvocationExpression(GetMethodNameSyntaxWithCurrentTypeParameters(fn), CreateArguments(new[] { SyntaxFactory.Argument(SyntaxFactory.IdentifierName(param.Identifier.ValueText)) }.Union(captures.Select(x => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(x.Name)).WithRef(x.Changes)))));
            }
        }

        private ExpressionSyntax GetMethodNameSyntaxWithCurrentTypeParameters(string fn)
        {
            return (currentMethodTypeParameters?.Parameters.Count).GetValueOrDefault() != 0 ? SyntaxFactory.GenericName(SyntaxFactory.Identifier(fn), SyntaxFactory.TypeArgumentList(CreateSeparatedList(currentMethodTypeParameters.Parameters.Select(x => SyntaxFactory.ParseTypeName(x.Identifier.ValueText))))) : (NameSyntax)SyntaxFactory.IdentifierName(fn);
        }

        private TypeDeclarationSyntax currentType;
        private TypeParameterListSyntax currentMethodTypeParameters;
        private SyntaxList<TypeParameterConstraintClauseSyntax> currentMethodConstraintClauses;


        public override SyntaxNode VisitQueryExpression(QueryExpressionSyntax node)
        {
            // LINQ query expressions are desugared to method-call chains
            // (Where/Select/OrderBy/GroupBy) by LinqQueryDesugarer before this
            // rewriter runs.  Any query expressions that reach here were not
            // desugared (e.g. complex cases with let/join/into) and are handled
            // by QueryExpressionTransformer during the Java emit phase.
            return base.VisitQueryExpression(node);
        }

        public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            return VisitTypeDeclaration(node);
        }

        private LocalDeclarationStatementSyntax CreateLocalVariableDeclaration(string name, ExpressionSyntax value)
        {
            return SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"), CreateSeparatedList(new[] { SyntaxFactory.VariableDeclarator(name).WithInitializer(SyntaxFactory.EqualsValueClause(value)) })));
        }

        private ITypeSymbol GetSymbolType(ISymbol x)
        {
            var local = x as ILocalSymbol;
            if (local != null) return local.Type;

            var param = x as IParameterSymbol;
            if (param != null) return param.Type;

            throw new NotImplementedException();
        }
        private static SeparatedSyntaxList<T> CreateSeparatedList<T>(IEnumerable<T> items) where T : SyntaxNode
        {
            return SyntaxFactory.SeparatedList<T>(items);
        }
        private static SeparatedSyntaxList<T> CreateSeparatedList<T>(params T[] items) where T : SyntaxNode
        {
            return SyntaxFactory.SeparatedList<T>(items);
        }
        private static ArgumentListSyntax CreateArguments(IEnumerable<ExpressionSyntax> items)
        {
            return CreateArguments(items.Select(x => SyntaxFactory.Argument(x)));
        }
        private static ArgumentListSyntax CreateArguments(params ExpressionSyntax[] items)
        {
            return CreateArguments((IEnumerable<ExpressionSyntax>)items);
        }
        private static ArgumentListSyntax CreateArguments(IEnumerable<ArgumentSyntax> items)
        {
            return SyntaxFactory.ArgumentList(CreateSeparatedList(items));
        }
        private static ParameterListSyntax CreateParameters(IEnumerable<ParameterSyntax> items)
        {
            return SyntaxFactory.ParameterList(CreateSeparatedList(items));
        }
        private static ParameterSyntax CreateParameter(SyntaxToken name, ITypeSymbol type)
        {
            return SyntaxFactory.Parameter(name).WithType(SyntaxFactory.ParseTypeName(type.ToDisplayString()));
        }
        private static ParameterSyntax CreateParameter(SyntaxToken name, TypeSyntax type)
        {
            return SyntaxFactory.Parameter(name).WithType(type);
        }
        private static ParameterSyntax CreateParameter(string name, ITypeSymbol type)
        {
            return CreateParameter(SyntaxFactory.Identifier(name), type);
        }
        private static ParameterSyntax CreateParameter(string name, TypeSyntax type)
        {
            return CreateParameter(SyntaxFactory.Identifier(name), type);
        }


        public Diagnostic CreateDiagnosticForException(Exception ex, string path)
        {
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            var message = "roslyn-linq-rewrite exception while processing '" + path + "', method " + currentMethodName + ": " + ex.Message + " -- " + ex.StackTrace?.Replace("\n", "");

            return Diagnostic.Create("LQRW1001", "Compiler", new LiteralString(message), Microsoft.CodeAnalysis.DiagnosticSeverity.Error, Microsoft.CodeAnalysis.DiagnosticSeverity.Error, true, 0);
        }

    }


}