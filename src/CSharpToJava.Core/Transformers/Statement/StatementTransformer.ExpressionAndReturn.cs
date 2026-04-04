using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    private JavaSyntaxNode TransformExpressionStatement(ExpressionStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // ConditionalAccessExpression (obj?.Method()) as a statement cannot be a ternary expression in Java.
        // Java only allows method calls, assignments, and new as expression statements.
        // Convert to: if (obj != null) { obj.Method(); }
        if (stmt.Expression is ConditionalAccessExpressionSyntax condAccess)
        {
            var objExpr = exprTransformer.Transform(condAccess.Expression, context);
            string innerCall;
            switch (condAccess.WhenNotNull)
            {
                case MemberBindingExpressionSyntax binding:
                    innerCall = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(binding.Name.Identifier.Text)};";
                    break;
                case InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax invokeBinding:
                    var args = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => exprTransformer.Transform(a.Expression, context)));
                    innerCall = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(invokeBinding.Name.Identifier.Text)}({args});";
                    break;
                default:
                    // General case: ?.a.b(...) — recursively substitute the member binding with objExpr
                    innerCall = exprTransformer.TransformWhenNotNull(condAccess.WhenNotNull, objExpr, context) + ";";
                    break;
            }
            return new JavaStatementNode($"if ({objExpr} != null) {{ {innerCall} }}");
        }

        // Special case: dict.TryGetValue(key, out var v) as a standalone statement → v = dict.get(key);
        // For value types (structs/primitives), use getOrDefault to avoid NPE since C# TryGetValue
        // default-initializes the out parameter when the key is not found.
        if (stmt.Expression is InvocationExpressionSyntax tvInvoc &&
            tvInvoc.Expression is MemberAccessExpressionSyntax tvMa &&
            tvMa.Name.Identifier.Text == "TryGetValue" &&
            tvInvoc.ArgumentList.Arguments.Count == 2)
        {
            var tvTarget = exprTransformer.Transform(tvMa.Expression, context);
            var tvKey = exprTransformer.Transform(tvInvoc.ArgumentList.Arguments[0].Expression, context);
            var tvArg2 = tvInvoc.ArgumentList.Arguments[1];
            if (tvArg2.Expression is DeclarationExpressionSyntax tvDecl2)
            {
                var declType = context.SemanticModel?.GetTypeInfo(tvDecl2.Type);
                var javaType = declType.HasValue && declType.Value.Type != null ? context.MapType(declType.Value.Type) : "var";
                var varName = tvDecl2.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : "_outVar";
                var defaultVal = GetValueTypeDefault(declType?.Type, javaType);
                var getCall = defaultVal != null
                    ? $"{tvTarget}.getOrDefault({tvKey}, {defaultVal})"
                    : $"{tvTarget}.get({tvKey})";
                return new JavaStatementNode($"{javaType} {varName} = {getCall};");
            }
            else
            {
                var tvOut2 = exprTransformer.Transform(tvArg2.Expression, context);
                var outTypeInfo = context.SemanticModel?.GetTypeInfo(tvArg2.Expression);
                string? defaultVal = null;
                if (outTypeInfo?.Type is { IsValueType: true } outType)
                {
                    defaultVal = GetValueTypeDefault(outType, context.MapType(outType));
                }
                var getCall = defaultVal != null
                    ? $"{tvTarget}.getOrDefault({tvKey}, {defaultVal})"
                    : $"{tvTarget}.get({tvKey})";
                return new JavaStatementNode($"{tvOut2} = {getCall};");
            }
        }

        // Array.Sort(keys, items) overload: sort keys and reorder items accordingly.
        if (stmt.Expression is InvocationExpressionSyntax arrSortInv
            && arrSortInv.Expression is MemberAccessExpressionSyntax arrSortMa
            && arrSortMa.Name.Identifier.Text == "Sort"
            && arrSortInv.ArgumentList.Arguments.Count == 2)
        {
            var receiverText = arrSortMa.Expression.ToString();
            bool isArrayReceiver = receiverText is "Array" or "System.Array";
            if (isArrayReceiver && context.SemanticModel != null)
            {
                var arg0Expr = arrSortInv.ArgumentList.Arguments[0].Expression;
                var arg1Expr = arrSortInv.ArgumentList.Arguments[1].Expression;
                var keyArr = context.SemanticModel.GetTypeInfo(arg0Expr).Type as IArrayTypeSymbol;
                var itemArr = context.SemanticModel.GetTypeInfo(arg1Expr).Type as IArrayTypeSymbol;

                bool keyIsNumeric = keyArr?.ElementType.SpecialType is
                    SpecialType.System_Byte or SpecialType.System_SByte
                    or SpecialType.System_Int16 or SpecialType.System_UInt16
                    or SpecialType.System_Int32 or SpecialType.System_UInt32
                    or SpecialType.System_Int64 or SpecialType.System_UInt64
                    or SpecialType.System_Single or SpecialType.System_Double
                    or SpecialType.System_Decimal;

                if (keyArr != null && itemArr != null && keyIsNumeric)
                {
                    var keysExpr = exprTransformer.Transform(arg0Expr, context);
                    var itemsExpr = exprTransformer.Transform(arg1Expr, context);
                    var keyElemType = context.MapType(keyArr.ElementType);
                    var itemElemType = context.MapType(itemArr.ElementType);

                    context.AddImport("java.util.Arrays");
                    context.AddImport("java.util.Comparator");
                    context.AddImport("java.util.stream.IntStream");

                    var idxName = context.GenerateSyntheticName("_sortIdx");
                    var keyCopyName = context.GenerateSyntheticName("_keyCopy");
                    var itemCopyName = context.GenerateSyntheticName("_itemCopy");

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Integer[] {idxName} = IntStream.range(0, {keysExpr}.length).boxed().toArray(Integer[]::new);");
                    sb.AppendLine($"Arrays.sort({idxName}, Comparator.comparingDouble(i -> (double){keysExpr}[i]));");
                    sb.AppendLine($"{keyElemType}[] {keyCopyName} = {keysExpr}.clone();");
                    sb.AppendLine($"{itemElemType}[] {itemCopyName} = {itemsExpr}.clone();");
                    sb.Append($"for (int i = 0; i < {idxName}.length; i++) {{ {keysExpr}[i] = {keyCopyName}[{idxName}[i]]; {itemsExpr}[i] = {itemCopyName}[{idxName}[i]]; }}");
                    return new JavaStatementNode(sb.ToString());
                }
            }
        }

        // Special case: Debug.Assert / Trace.Assert / Contract.Requires / Contract.Assert
        // Java's 'assert' is a statement keyword, not a callable method — emit it directly
        // so the call never reaches the generic name-lowering + EscapeJavaKeyword path that
        // would produce the wrong "System.assertValue(...)" output.
        if (stmt.Expression is InvocationExpressionSyntax assertInvoc &&
            assertInvoc.Expression is MemberAccessExpressionSyntax assertMa &&
            assertInvoc.ArgumentList.Arguments.Count >= 1 &&
            assertMa.Name.Identifier.Text is "Assert" or "Requires")
        {
            bool isAssertLike = false;
            if (context.SemanticModel != null &&
                context.SemanticModel.GetSymbolInfo(assertInvoc).Symbol is IMethodSymbol assertSym)
            {
                var typeName = assertSym.ContainingType.ToDisplayString();
                isAssertLike = typeName is "System.Diagnostics.Debug"
                                         or "System.Diagnostics.Trace"
                                         or "System.Diagnostics.Contracts.Contract";
            }
            if (!isAssertLike)
            {
                // Syntactic fallback: semantic model absent or couldn't resolve the symbol
                var receiver = assertMa.Expression.ToString();
                isAssertLike = receiver is "Debug" or "Trace" or "Contract"
                                         or "System.Diagnostics.Debug"
                                         or "System.Diagnostics.Trace"
                                         or "System.Diagnostics.Contracts.Contract";
            }

            if (isAssertLike)
            {
                var condition = exprTransformer.Transform(assertInvoc.ArgumentList.Arguments[0].Expression, context);
                string assertStmt;
                if (assertInvoc.ArgumentList.Arguments.Count >= 2)
                {
                    var message = exprTransformer.Transform(assertInvoc.ArgumentList.Arguments[1].Expression, context);
                    assertStmt = $"assert {condition} : {message};";
                }
                else
                {
                    assertStmt = $"assert {condition};";
                }

                // Drain any pre/post statements emitted during condition/message transformation
                // (e.g., ref/out Holder declarations must precede the assert statement)
                if (context.HasPendingPreStatements || context.HasPendingPostStatements)
                {
                    var sb = new System.Text.StringBuilder();
                    if (context.HasPendingPreStatements)
                    {
                        var preStmts = context.DrainPreStatements();
                        sb.AppendLine(string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")));
                    }
                    sb.Append(assertStmt);
                    if (context.HasPendingPostStatements)
                    {
                        var postStmts = context.DrainPostStatements();
                        sb.Append("\n" + string.Join("\n", postStmts.Select(s => s.TrimEnd(';') + ";")));
                    }
                    return new JavaStatementNode(sb.ToString());
                }

                return new JavaStatementNode(assertStmt);
            }
        }

        // Fix 4: Tuple deconstruction — var (first, second) = GetPair();
        // ExpressionStatement > AssignmentExpression where LHS is DeclarationExpression with ParenthesizedVariableDesignation
        if (stmt.Expression is AssignmentExpressionSyntax tupleAssign
            && tupleAssign.Left is DeclarationExpressionSyntax tupleDecl
            && tupleDecl.Designation is ParenthesizedVariableDesignationSyntax parenDesig)
        {
            var rhsExpr = exprTransformer.Transform(tupleAssign.Right, context);
            var tempVar = "_t";
            var names = parenDesig.Variables
                .OfType<SingleVariableDesignationSyntax>()
                .Select(svd => svd.Identifier.Text)
                .ToList();
            var sbTuple = new System.Text.StringBuilder();
            sbTuple.AppendLine($"var {tempVar} = {rhsExpr};");
            for (int i = 0; i < names.Count; i++)
            {
                string getter = $"_{i + 1}";
                if (i < names.Count - 1)
                    sbTuple.AppendLine($"var {ConversionContext.EscapeJavaKeyword(names[i])} = {tempVar}.{getter}();");
                else
                    sbTuple.Append($"var {ConversionContext.EscapeJavaKeyword(names[i])} = {tempVar}.{getter}();");
            }
            return new JavaStatementNode(sbTuple.ToString());
        }

        var expr = exprTransformer.Transform(stmt.Expression, context);

        // Drain any pre-statements emitted by the expression transformer
        // (e.g., chain property assignment: list.Add(obj.Prop = local = expr) splits into pre-stmts)
        // Also drain post-statements (e.g., out-param holder reads back into variables after the call)
        if (context.HasPendingPreStatements)
        {
            var preStmts = context.DrainPreStatements();
            var preStmtLines = string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";"));
            // If the main expression is just a simple variable or an out-param holder field access
            // (from chain-assignment hoisting), skip it as a statement — e.g. "anchors.value;" is
            // not valid Java. Valid Java statements must be method calls, assignments, etc.
            bool isNonStatement = expr.All(c => char.IsLetterOrDigit(c) || c == '_')
                || expr.EndsWith(".value");  // out-param XHolder field access (e.g. anchors.value)
            string stmtBlock = isNonStatement ? preStmtLines : preStmtLines + "\n" + expr + ";";
            if (context.HasPendingPostStatements)
            {
                var postStmts = context.DrainPostStatements();
                stmtBlock += "\n" + string.Join("\n", postStmts.Select(s => s.TrimEnd(';') + ";"));
            }
            return new JavaStatementNode(stmtBlock);
        }

        if (context.HasPendingPostStatements)
        {
            var postStmts = context.DrainPostStatements();
            var postStmtLines = string.Join("\n", postStmts.Select(s => s + ";"));
            return new JavaStatementNode(expr + ";\n" + postStmtLines);
        }

        return new JavaStatementNode(expr + ";");
    }

    private JavaSyntaxNode TransformThrowStatement(ThrowStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            // Java does not support bare 'throw;' — rethrow the enclosing catch variable
            // Fix 2: Stop ancestor walk at lambda boundaries to avoid crossing exception scope
            var catchVar = stmt.Ancestors()
                .TakeWhile(a => a is not AnonymousFunctionExpressionSyntax)
                .OfType<CatchClauseSyntax>()
                .FirstOrDefault();
            if (catchVar == null)
                return new JavaStatementNode("throw; /* TODO: rethrow outside catch - manual conversion required */");
            var rethrowName = catchVar.Declaration?.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(rethrowName)) rethrowName = "_ex";
            return new JavaStatementNode($"throw {ConversionContext.EscapeJavaKeyword(rethrowName.Trim())};");
        }
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);
        return new JavaStatementNode($"throw {expr};");
    }

    private JavaSyntaxNode TransformReturnStatement(ReturnStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            return new JavaStatementNode("return;");
        }

        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);
        var returnTargetType = ResolveReturnTargetType(stmt, context);

        // Detect when a C# array element (from jagged array) is returned where a List<T> is expected.
        // e.g., return outEdges[vertex]; where the method returns IList<TEdge> → List<TEdge> in Java
        // and outEdges is TEdge[][] — element is TEdge[] which doesn't implement List<TEdge> in Java.
        if (stmt.Expression != null && context.SemanticModel != null)
        {
            var exprType = context.SemanticModel.GetTypeInfo(stmt.Expression).Type;
            if (exprType is IArrayTypeSymbol { Rank: 1 } arrayType)
            {
                // Check if the enclosing method's return type is IList<T>, ICollection<T>, or IEnumerable<T>
                var enclosingMethod = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetSym = null;
                if (enclosingMethod != null)
                    enclosingRetSym = context.SemanticModel.GetTypeInfo(enclosingMethod.ReturnType).Type;
                // Also check property accessor (return in a get { } block)
                if (enclosingRetSym == null)
                {
                    var propAccessor = stmt.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
                    if (propAccessor != null)
                    {
                        var propDecl = propAccessor.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (propDecl != null)
                            enclosingRetSym = context.SemanticModel.GetTypeInfo(propDecl.Type).Type;
                    }
                }
                if (enclosingRetSym is INamedTypeSymbol retNamed &&
                    retNamed.Name is "IList" or "ICollection" or "List" or "Collection"
                        or "IEnumerable" or "Iterable")
                {
                    // Primitive arrays (int[], double[], etc.) can't use Arrays.asList() directly
                    // because Arrays.asList(int[]) returns List<int[]>, not List<Integer>.
                    if (arrayType.ElementType.SpecialType is
                        SpecialType.System_Int32 or SpecialType.System_Int64 or
                        SpecialType.System_Double or SpecialType.System_Single or
                        SpecialType.System_Boolean or SpecialType.System_Byte or
                        SpecialType.System_Int16 or SpecialType.System_Char)
                    {
                        expr = $"Arrays.stream({expr}).boxed().collect(java.util.stream.Collectors.toList())";
                        context.AddImport("java.util.Arrays");
                        context.AddImport("java.util.stream.Collectors");
                    }
                    else
                    {
                        expr = $"Arrays.asList({expr})";
                        context.AddImport("java.util.Arrays");
                    }
                }
            }

            // Detect when a Stream expression is returned from a method that declares Iterable/IEnumerable.
            // C# LINQ expressions become Java Streams but IEnumerable<T> maps to Iterable<T>.
            // Stream<T> does not implement Iterable<T>, so we need .collect(Collectors.toCollection(() -> new ArrayList<>())).
            var retExprType = context.SemanticModel?.GetTypeInfo(stmt.Expression).Type;
            bool isStreamReturn = retExprType is INamedTypeSymbol retNamed2 &&
                (retNamed2.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable") &&
                retNamed2.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
            if (isStreamReturn && (expr.Contains(".map(") || expr.Contains(".filter(") ||
                expr.Contains(".flatMap(") || expr.Contains(".select(") ||
                expr.Contains("stream(") || expr.Contains("Stream.concat") ||
                expr.Contains(".distinct(") || expr.Contains(".sorted(")))
            {
                // Check if enclosing method returns Iterable
                var enclosing = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetType = enclosing != null
                    ? context.SemanticModel?.GetTypeInfo(enclosing.ReturnType).Type
                    : null;
                // Also check property accessor (return inside a get { } block)
                if (enclosingRetType == null)
                {
                    var accessorDecl = stmt.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
                    if (accessorDecl != null)
                    {
                        var propDecl2 = accessorDecl.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (propDecl2 != null)
                            enclosingRetType = context.SemanticModel?.GetTypeInfo(propDecl2.Type).Type;
                    }
                }
                bool enclosingReturnsIterable = enclosingRetType is INamedTypeSymbol mret &&
                    mret.Name is "IEnumerable" or "ICollection" or "IList";
                // Don't double-collect: if the expression already ends with .toList() or ArrayList<>()) it's already a List
                bool alreadyCollected = expr.EndsWith(".toList())")
                    || expr.EndsWith("toList()))")
                    || expr.EndsWith("new ArrayList<>()))")
                    || expr.EndsWith("new ArrayList<>())")
                    || expr.EndsWith(".toArray())")
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\.toArray\([^)]+\)\)$")
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\.toArray\([^)]+\)$");
                if (enclosingReturnsIterable && !alreadyCollected)
                {
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    expr = $"{expr}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
                }
            }
        }

        // Struct value copy: when returning a user-defined struct expression that is not a temporary,
        // clone it to preserve C# value-copy semantics (C# return always copies structs).
        // Skip inside property getters where the consumption site handles cloning.
        if (stmt.Expression != null && context.SemanticModel != null && !context.SuppressReturnClone)
        {
            var retExprTypeForClone = context.SemanticModel.GetTypeInfo(stmt.Expression).Type;
            expr = StructCloneHelper.CloneStructValueIfNeeded(stmt.Expression, expr, retExprTypeForClone, context);
        }

        expr = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
            stmt.Expression,
            expr,
            returnTargetType,
            context);

        // Bug 3: drain any pre/post statements produced while transforming the return expression
        // (e.g., ref argument wrapping adds pre-statements for holder init and post-statements for write-back).
        if (context.HasPendingPreStatements || context.HasPendingPostStatements)
        {
            var sb = new System.Text.StringBuilder();
            if (context.HasPendingPreStatements)
            {
                var preStmts = context.DrainPreStatements();
                sb.Append(string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")));
                sb.Append("\n");
            }
            if (context.HasPendingPostStatements)
            {
                // Capture the return value in a temp variable, emit post-stmts, then return it.
                var postStmts = context.DrainPostStatements();
                sb.Append($"var _ret = {expr};\n");
                sb.Append(string.Join("\n", postStmts.Select(s => s.TrimEnd(';') + ";")));
                sb.Append("\nreturn _ret;");
            }
            else
            {
                sb.Append($"return {expr};");
            }
            return new JavaStatementNode(sb.ToString());
        }

        return new JavaStatementNode($"return {expr};");
    }

    private static ITypeSymbol? ResolveReturnTargetType(ReturnStatementSyntax stmt, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return null;

        var returnScope = stmt.Ancestors().FirstOrDefault(ancestor => ancestor is MethodDeclarationSyntax
            or AccessorDeclarationSyntax
            or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax);

        return returnScope switch
        {
            MethodDeclarationSyntax method => context.SemanticModel.GetTypeInfo(method.ReturnType).Type,
            AccessorDeclarationSyntax accessor => (context.SemanticModel.GetDeclaredSymbol(accessor) as IMethodSymbol)?.ReturnType,
            LocalFunctionStatementSyntax localFunction => context.SemanticModel.GetTypeInfo(localFunction.ReturnType).Type,
            _ => null
        };
    }
}
