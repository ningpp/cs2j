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
    /// <summary>
    /// Returns true when <paramref name="javaType"/> is a Java collection/list type whose
    /// constructor or assignment slot requires a <c>Collection</c>-compatible value.
    /// Excludes <c>Iterable</c> because that is already replaced by <c>var</c> earlier.
    /// </summary>
    private static bool IsJavaCollectionOrListType(string javaType)
    {
        // An array of collections (e.g. ArrayList<Integer>[]) is NOT itself a collection type.
        if (javaType.TrimEnd().EndsWith("[]"))
            return false;
        var bare = javaType.Contains('<') ? javaType[..javaType.IndexOf('<')] : javaType;
        return bare is "List" or "Collection" or "ArrayList" or "HashSet" or "TreeSet"
            or "LinkedList" or "LinkedHashSet" or "ArrayDeque" or "Stack" or "Vector"
            or "Set" or "Deque" or "Queue";
    }

    /// <summary>
    /// Checks if an expression ends with a balanced .collect(...) call.
    /// Unlike a simple regex, this correctly handles nested parentheses so
    /// expressions like ".collect(groupingBy(...)).entrySet()" are NOT detected.
    /// </summary>
    private static bool EndsWithCollectCall(string expr)
    {
        if (!expr.EndsWith(")"))
            return false;
        // Walk backward to find the matching '(' for the final ')'
        int depth = 0;
        int i = expr.Length - 1;
        for (; i >= 0; i--)
        {
            if (expr[i] == ')') depth++;
            else if (expr[i] == '(') depth--;
            if (depth == 0) break;
        }
        // i now points to the matching '(' — check if preceded by ".collect"
        const string collectSuffix = ".collect";
        return i >= collectSuffix.Length
            && expr.AsSpan((i - collectSuffix.Length), collectSuffix.Length).SequenceEqual(collectSuffix.AsSpan());
    }

    /// <summary>
    /// Checks whether an expression contains a lambda or method reference.
    /// Used to detect ternary expressions that Java var can't infer.
    /// </summary>
    private static bool ContainsLambdaOrMethodRef(ExpressionSyntax expr)
    {
        if (expr is ParenthesizedExpressionSyntax paren)
            return ContainsLambdaOrMethodRef(paren.Expression);
        if (expr is CastExpressionSyntax cast)
            // A cast in a ternary branch (e.g., (DelegateType)MethodGroup) signals delegate conversion.
            // Return true conservatively — forces explicit type instead of var, which is always safe.
            return true;
        return expr is SimpleLambdaExpressionSyntax
            or ParenthesizedLambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax
            || expr is MemberAccessExpressionSyntax;
    }

    /// <summary>
    /// Returns a Java default-value expression for a C# value type, or null for reference types.
    /// Used to generate getOrDefault() calls for TryGetValue on value-type dictionary values.
    /// </summary>
    private static string? GetValueTypeDefault(ITypeSymbol? typeSymbol, string javaTypeName)
    {
        if (typeSymbol == null || !typeSymbol.IsValueType)
            return null;

        return javaTypeName switch
        {
            "int" => "0",
            "long" => "0L",
            "short" => "(short)0",
            "byte" => "(byte)0",
            "float" => "0.0f",
            "double" => "0.0",
            "boolean" => "false",
            "char" => "'\\0'",
            "Decimal" => "Decimal.ZERO",
            _ => $"new {javaTypeName}()"
        };
    }

    /// <summary>
    /// Converts a string to PascalCase by capitalizing the first letter.
    /// Used for generating JavaBean-compliant getter method names.
    /// </summary>
    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpper(name[0]) + name.Substring(1);
    }

    /// <summary>
    /// Pre-scans a method body block to identify local variables that are captured by lambdas
    /// and also externally reassigned. For such variables, registers a pending holder mapping
    /// so that a <c>Type[] _varName = { varName }</c> declaration is emitted right after
    /// the variable declaration (or right before the loop for for-loop iteration variables),
    /// and all subsequent references are replaced with <c>_varName[0]</c>.
    /// <para>
    /// This handles the Java effectively-final constraint for the case where a variable is
    /// reassigned outside the lambda (the existing <c>GetMutatedCaptures</c> in LambdaTransformer
    /// only handles mutations inside the lambda body).
    /// </para>
    /// </summary>
    private static void PreScanLambdaCaptures(BlockSyntax block, ConversionContext context)
    {
        if (context.SemanticModel == null) return;

        // Collect all lambda and anonymous method nodes in the method body
        var lambdas = block.DescendantNodes()
            .Where(n => n is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            .ToList();

        if (lambdas.Count == 0) return;

        // For each lambda, collect captured local variable names
        // Use a list to handle variable shadowing (same name, different symbols)
        var capturedLocals = new List<(string Name, ILocalSymbol Symbol)>();
        var seenSymbols = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);

        foreach (var lambda in lambdas)
        {
            // Collect lambda/anonymous-method parameter names to exclude
            var paramNames = lambda switch
            {
                SimpleLambdaExpressionSyntax s => new HashSet<string> { s.Parameter.Identifier.Text },
                ParenthesizedLambdaExpressionSyntax p =>
                    new HashSet<string>(p.ParameterList.Parameters.Select(pm => pm.Identifier.Text)),
                AnonymousMethodExpressionSyntax a when a.ParameterList != null =>
                    new HashSet<string>(a.ParameterList.Parameters.Select(pm => pm.Identifier.Text)),
                _ => new HashSet<string>()
            };

            // For anonymous methods, the body is always a Block; for lambdas, block or expression
            SyntaxNode? body = lambda switch
            {
                LambdaExpressionSyntax l => (SyntaxNode?)l.Block ?? l.ExpressionBody,
                AnonymousMethodExpressionSyntax am => am.Block,
                _ => null
            };
            if (body == null) continue;

            foreach (var node in body.DescendantNodesAndSelf())
            {
                if (node is IdentifierNameSyntax id && !paramNames.Contains(id.Identifier.Text))
                {
                    var symbol = context.GetSymbolInfo(id).Symbol;
                    if (symbol is ILocalSymbol local && !seenSymbols.Contains(local))
                    {
                        // Confirm the declaration is outside the lambda span
                            var declLocation = local.Locations.FirstOrDefault();
                            if (declLocation != null && !lambda.Span.Contains(declLocation.SourceSpan))
                            {
                                seenSymbols.Add(local);
                                capturedLocals.Add((local.Name, local));
                            }
                    }
                }
            }
        }

        if (capturedLocals.Count == 0) return;

        // Build a set of all lambda/anonymous-method spans for O(1) containment check
        var lambdaSpanSet = new HashSet<SyntaxNode>(lambdas);
        static bool IsInsideAnyLambda(SyntaxNode node, HashSet<SyntaxNode> lambdaNodes)
        {
            var current = node.Parent;
            while (current != null)
            {
                if (lambdaNodes.Contains(current))
                    return true;
                current = current.Parent;
            }
            return false;
        }

        // For each captured local, check if it is reassigned outside any lambda
        foreach (var (varName, localSymbol) in capturedLocals)
        {
            bool isExternallyReassigned = false;

            foreach (var node in block.DescendantNodes())
            {
                // Check if this node is inside any lambda body — if so, skip
                if (IsInsideAnyLambda(node, lambdaSpanSet))
                    continue;

                // Detect reassignment: assignment target, prefix/postfix ++/--, and ref/out arguments.
                // ref/out arguments mutate the captured variable (the callee writes back),
                // so they must be treated as external reassignments for holder replacement.
                IdentifierNameSyntax? targetId = node switch
                {
                    AssignmentExpressionSyntax assign when assign.Left is IdentifierNameSyntax aid => aid,
                    PrefixUnaryExpressionSyntax pre when
                        (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression)) &&
                        pre.Operand is IdentifierNameSyntax pid => pid,
                    PostfixUnaryExpressionSyntax post when
                        (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression)) &&
                        post.Operand is IdentifierNameSyntax ppid => ppid,
                    ArgumentSyntax arg when
                        (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) &&
                        arg.Expression is IdentifierNameSyntax aid => aid,
                    _ => null
                };

                if (targetId == null) continue;
                if (targetId.Identifier.Text != varName) continue;

                // Verify it refers to the same local symbol
                var refSymbol = context.GetSymbolInfo(targetId).Symbol;
                if (refSymbol is ILocalSymbol refLocal &&
                    SymbolEqualityComparer.Default.Equals(refLocal, localSymbol))
                {
                    isExternallyReassigned = true;
                    break;
                }
            }

            if (isExternallyReassigned)
            {
                var javaType = context.MapType(localSymbol.Type);
                var declaringSyntax = localSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                if (declaringSyntax != null)
                    context.MethodState.RegisterPendingLambdaCaptureHolder(varName, javaType, declaringSyntax);
            }
        }
    }

}
