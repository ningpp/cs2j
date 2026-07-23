using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using CSharpToJava.Core.Transformers.Statement;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles lambda and anonymous method expressions.
/// </summary>
[TransformerRegistration]
public class LambdaTransformer : IIRExpressionTransformer
{
    static LambdaTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.AnonymousMethodExpression
        }, new LambdaTransformer());
    }

    private static readonly Lazy<LambdaTransformer> _instance = new(() => new());
    public static LambdaTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.ParenthesizedLambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.SimpleLambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.AnonymousMethodExpression => TransformAnonymousMethod((AnonymousMethodExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Lambda expression kind {node.Kind()} not supported.")
        };

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        // For simple non-async, non-capture-mutating lambdas, produce JavaLambdaExpression
        if (node is LambdaExpressionSyntax lambda)
        {
            bool isAsync = lambda.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
            if (!isAsync && lambda.ExpressionBody != null)
            {
                var mutatedCaptures = GetMutatedCaptures(lambda, context);
                if (mutatedCaptures.Count == 0)
                {
                    // Collect parameters
                    IEnumerable<ParameterSyntax> parameters = lambda switch
                    {
                        SimpleLambdaExpressionSyntax simple => [simple.Parameter],
                        ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters,
                        _ => []
                    };

                    var javaParams = parameters
                        .Select(p =>
                        {
                            string name = ConversionContext.EscapeJavaKeyword(p.Identifier.Text);
                            if (p.Type != null)
                            {
                                string javaType = ExpressionTransformerHelpers.BoxJavaPrimitiveType(
                                    context.MapTypeFromSyntax(p.Type));
                                return $"{javaType} {name}";
                            }
                            return name;
                        })
                        .ToList();

                    var facade = ExpressionTransformerFacade.Instance;

                    // Check for pre-statements from expression body
                    bool hadPendingPreBefore = context.HasPendingPreStatements;
                    var bodyIR = facade.TransformToIR(lambda.ExpressionBody, context);

                    bool hasNewPreStatements = !hadPendingPreBefore && context.HasPendingPreStatements;
                    if (!hasNewPreStatements)
                    {
                        var lambdaIR = new JavaLambdaExpression
                        {
                            ExpressionBody = bodyIR
                        };
                        lambdaIR.Parameters.AddRange(javaParams);
                        return lambdaIR;
                    }
                    // If pre-statements were generated, fall through to raw
                    // (the pre-statements are already drained from context)
                }
            }
        }

        return new JavaRawExpression(Transform(node, context));
    }

    private string TransformLambda(LambdaExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Fix 4: Detect static lambdas (C# 9) — static has no Java equivalent; strip it silently
        bool isStaticLambda = node.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

        // Fix 1: Detect async lambdas
        bool isAsync = node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));

        // Fix 2: Collect parameters, emitting explicit types when present
        IEnumerable<ParameterSyntax> parameters = node switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter],
            ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters,
            _ => []
        };

        var javaParams = parameters
            .Select(p =>
            {
                string name = ConversionContext.EscapeJavaKeyword(p.Identifier.Text);
                // Fix 2: emit explicit type when the parameter carries a declared type
                if (p.Type != null)
                {
                    string javaType = ExpressionTransformerHelpers.BoxJavaPrimitiveType(
                        context.MapTypeFromSyntax(p.Type));
                    return $"{javaType} {name}";
                }
                return name;
            })
            .ToList();

        var paramStr = FormatLambdaParameterList(javaParams);

        // Fix 3: Detect mutated captured variables and add array-holder pre-statements
        var mutatedCaptures = GetMutatedCaptures(node, context);
        foreach (var (capName, capType) in mutatedCaptures)
        {
            // Skip variables already handled by the lambda capture pre-scan
            // (externally reassigned variables get their holder from StatementTransformer)
            // Only skip if the holder is active (declared) — pending holders for for-loop
            // variables may never be activated, so GetMutatedCaptures must still handle them.
            if (context.MethodState.HasActiveLambdaCaptureHolder(capName))
                continue;
            context.AddPreStatement(ExpressionTransformerHelpers.BuildLambdaCaptureHolderDeclaration(capType, $"_{capName}", capName));
            // Register the holder as active so that IdentifierExpressionTransformer
            // replaces all subsequent references to capName with _capName[0] in the
            // enclosing scope (after the lambda). Without this, reads after the lambda
            // still see the stale original local value.
            context.MethodState.RegisterActiveLambdaCaptureHolder(capName, $"_{capName}");
        }

        // Register captures in the lambda capture registry for downstream rewriter queries
        if (mutatedCaptures.Count > 0)
        {
            var lambdaKey = $"{node.SpanStart}:{node.Span.Length}";
            var captureInfos = mutatedCaptures
                .Select(c => new MethodConversionState.CaptureInfo(c.Name, c.JavaType, IsMutable: true))
                .ToList();
            context.MethodState.RegisterLambdaCaptures(lambdaKey, captureInfos);
        }

        // Generate body
        if (node.Block != null)
        {
            // Save pending pre-statements before processing the block body.
            // Array-holder pre-statements added above must be emitted at the
            // enclosing scope (before the statement containing this lambda),
            // not inside the lambda body. TransformBlock drains all pending
            // pre-statements, so we preserve and restore them here.
            var savedPre = context.HasPendingPreStatements
                ? context.DrainPreStatements().ToList()
                : new List<string>();

            var stmtTransformer = new StatementTransformer();
            string body = stmtTransformer.TransformBlock(node.Block, context);

            // Restore saved pre-statements for the enclosing scope
            foreach (var ps in savedPre)
                context.AddPreStatement(ps);

            // Fix 3: replace mutated captured variable usages with array-element access
            // Always apply regex replacement even when HasLambdaCaptureHolder is true,
            // because out-parameter write-backs are raw strings not processed by
            // IdentifierExpressionTransformer. The regex \bX\b won't match already-replaced
            // _X[0] references (underscore is a word character), so this is safe.
            foreach (var (capName, _) in mutatedCaptures)
            {
                body = Regex.Replace(body, $@"\b{Regex.Escape(capName)}\b", $"_{capName}[0]");
                // Avoid self-referential holder initialization after replacement:
                // int[] _x = { _x[0] };  -> int[] _x = { x };
                body = Regex.Replace(
                    body,
                    $@"(_{Regex.Escape(capName)}\s*=\s*\{{\s*)_{Regex.Escape(capName)}\[0\](\s*\}})",
                    $"$1{capName}$2");
            }

            string result = $"{paramStr} -> {{\n{body}\n}}";

            // Fix 1: wrap async block-body lambdas in CompletableFuture
            if (isAsync)
            {
                context.AddImport("java.util.concurrent.CompletableFuture");
                bool isVoid = IsVoidAsyncLambda(node, context);
                result = isVoid
                    ? $"CompletableFuture.runAsync(() -> {{\n{body}\n}})"
                    : $"CompletableFuture.supplyAsync(() -> {{\n{body}\n}})";
            }

            // Fix 4: add comment for static lambdas
            if (isStaticLambda)
                result = $"/* C# static lambda — does not capture outer scope */ {result}";

            return result;
        }
        else if (node.ExpressionBody != null)
        {
            bool hadPendingPreBefore = context.HasPendingPreStatements;
            string body = facade.Transform(node.ExpressionBody, context);

            // Fix 5: Handle pre-statements generated by expression transformation
            // (e.g., object initializers that need to be expanded into multiple statements)
            var preStatements = (!hadPendingPreBefore && context.HasPendingPreStatements)
                ? context.DrainPreStatements().ToList()
                : new List<string>();

            // Handle post-statements generated by expression transformation
            // (e.g., ref parameter write-backs like "index = _indexRef.value").
            // These must be embedded INSIDE the lambda body because they reference
            // variables declared in pre-statements (which are inside the lambda).
            var postStatements = context.HasPendingPostStatements
                ? context.DrainPostStatements().ToList()
                : new List<string>();

            // Fix 3: replace mutated captured variable usages with array-element access
            // Skip variables already handled by the lambda capture pre-scan — their identifiers
            // are replaced by IdentifierExpressionTransformer during expression processing.
            // However, pre-statements and post-statements are raw strings that don't go through
            // IdentifierExpressionTransformer, so they always need replacement.
            foreach (var (capName, _) in mutatedCaptures)
            {
                bool hasHolder = context.MethodState.HasActiveLambdaCaptureHolder(capName);
                if (!hasHolder)
                {
                    body = Regex.Replace(body, $@"\b{Regex.Escape(capName)}\b", $"_{capName}[0]");
                    body = Regex.Replace(
                        body,
                        $@"(_{Regex.Escape(capName)}\s*=\s*\{{\s*)_{Regex.Escape(capName)}\[0\](\s*\}})",
                        $"$1{capName}$2");
                }
                // Always replace in pre-statements and post-statements, even when the holder
                // is active, because these are raw strings not processed by IdentifierExpressionTransformer.
                for (int i = 0; i < preStatements.Count; i++)
                {
                    preStatements[i] = Regex.Replace(preStatements[i], $@"\b{Regex.Escape(capName)}\b", $"_{capName}[0]");
                    preStatements[i] = Regex.Replace(
                        preStatements[i],
                        $@"(_{Regex.Escape(capName)}\s*=\s*\{{\s*)_{Regex.Escape(capName)}\[0\](\s*\}})",
                        $"$1{capName}$2");
                }
                for (int i = 0; i < postStatements.Count; i++)
                {
                    postStatements[i] = Regex.Replace(postStatements[i], $@"\b{Regex.Escape(capName)}\b", $"_{capName}[0]");
                    postStatements[i] = Regex.Replace(
                        postStatements[i],
                        $@"(_{Regex.Escape(capName)}\s*=\s*\{{\s*)_{Regex.Escape(capName)}\[0\](\s*\}})",
                        $"$1{capName}$2");
                }
            }

            string result;

            // Fix 6: Pre-statements don't have semicolons - add them when embedding in block
            var preStatementsWithSemis = preStatements.Select(s =>
                string.IsNullOrWhiteSpace(s) ? s : (s.EndsWith(";") ? s : s + ";")).ToList();
            var postStatementsWithSemis = postStatements.Select(s =>
                string.IsNullOrWhiteSpace(s) ? s : (s.EndsWith(";") ? s : s + ";")).ToList();

            bool hasExtraStatements = preStatements.Count > 0 || postStatements.Count > 0;

            // Fix 1: wrap async expression-body lambdas in CompletableFuture
            if (isAsync)
            {
                context.AddImport("java.util.concurrent.CompletableFuture");
                bool isVoid = IsVoidAsyncLambda(node, context);
                string asyncBody;
                if (hasExtraStatements)
                {
                    var preBlock = string.Join("\n", preStatementsWithSemis);
                    var postBlock = string.Join("\n", postStatementsWithSemis);
                    asyncBody = $"{{\n{preBlock}\nreturn {body};\n{postBlock}\n}}";
                }
                else
                {
                    asyncBody = body;
                }
                result = isVoid
                    ? $"CompletableFuture.runAsync(() -> {asyncBody})"
                    : $"CompletableFuture.supplyAsync(() -> {asyncBody})";
            }
            else
            {
                // Detect whether this lambda targets a void-returning delegate (Action, Consumer, etc.)
                bool isVoidLambda = false;
                if (context.SemanticModel != null)
                {
                    var convertedType = context.GetTypeInfo(node).ConvertedType;
                    if (convertedType is INamedTypeSymbol namedType)
                    {
                        var invokeMethod = namedType.DelegateInvokeMethod;
                        if (invokeMethod != null && invokeMethod.ReturnsVoid)
                            isVoidLambda = true;
                    }
                }

                // If there are pre-statements or post-statements, convert to block body
                if (hasExtraStatements)
                {
                    var preBlock = string.Join("\n", preStatementsWithSemis);
                    var postBlock = string.Join("\n", postStatementsWithSemis);
                    if (isVoidLambda)
                    {
                        // If the body is just a simple variable or an out-param holder field access
                        // (from chain-assignment hoisting), skip it — e.g. "_chainVal1;" or "anchors.value;"
                        // are not valid Java statements.
                        bool isNonStatement = IsBareReadExpression(body);
                        string bodyStmt = isNonStatement ? "" : $"\n{body};";
                        result = $"{paramStr} -> {{\n{preBlock}{bodyStmt}\n{postBlock}\n}}";
                    }
                    else
                        result = $"{paramStr} -> {{\n{preBlock}\nvar __result = {body};\n{postBlock}\nreturn __result;\n}}";
                }
                else
                {
                    // If the lambda target's return type is Iterable/Collection-like but the body
                    // is an uncollected stream pipeline, materialize it with .collect().
                    // Java's Stream<T> does NOT implement Iterable<T>, unlike C# IEnumerable<T>.
                    if (!isVoidLambda && context.SemanticModel != null)
                    {
                        var convertedType = context.GetTypeInfo(node).ConvertedType;
                        if (convertedType is INamedTypeSymbol namedType)
                        {
                            var invokeMethod = namedType.DelegateInvokeMethod;
                            if (invokeMethod != null && !invokeMethod.ReturnsVoid)
                            {
                                var retType = invokeMethod.ReturnType;
                                var retDisplay = retType?.OriginalDefinition.ToDisplayString();
                                bool expectsIterable = retDisplay is
                                    "System.Collections.Generic.IEnumerable<T>" or
                                    "System.Collections.IEnumerable" or
                                    "System.Collections.Generic.ICollection<T>" or
                                    "System.Collections.Generic.IList<T>";
                                if (expectsIterable && LooksLikeUncollectedStream(body))
                                {
                                    context.AddImport("java.util.stream.Collectors");
                                    context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                                    body = $"{body}.collect(CSharpList.toCSharpList())";
                                }
                            }
                        }
                    }
                    result = $"{paramStr} -> {body}";
                }
            }

            // Fix 4: add comment for static lambdas
            if (isStaticLambda)
                result = $"/* C# static lambda — does not capture outer scope */ {result}";

            return result;
        }

        return $"{paramStr} -> null";
    }

    private string TransformAnonymousMethod(AnonymousMethodExpressionSyntax node, ConversionContext context)
    {
        var stmtTransformer = new StatementTransformer();

        // Fix 5: Emit () for anonymous methods with null or empty parameter list
        string paramStr;
        if (node.ParameterList != null && node.ParameterList.Parameters.Count > 0)
        {
            var javaParams = node.ParameterList.Parameters
                .Select(p => ConversionContext.EscapeJavaKeyword(p.Identifier.Text))
                .ToList();
            paramStr = $"({string.Join(", ", javaParams)})";
        }
        else
        {
            paramStr = "()";
        }

        var body = stmtTransformer.TransformBlock(node.Block, context);
        return $"{paramStr} -> {{\n{body}\n}}";
    }

    private static string FormatLambdaParameterList(IReadOnlyList<string> javaParams)
    {
        return javaParams.Count == 1 && IsBareLambdaParameterName(javaParams[0])
            ? javaParams[0]
            : $"({string.Join(", ", javaParams)})";
    }

    private static bool IsBareLambdaParameterName(string parameter)
    {
        return IsIdentifierLikeSegment(parameter);
    }

    private static bool IsBareReadExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;

        var trimmed = expr.Trim();
        return trimmed.EndsWith(".value", StringComparison.Ordinal)
            || trimmed.Split('.').All(IsIdentifierLikeSegment);
    }

    private static bool IsIdentifierLikeSegment(string segment)
    {
        return segment.Length > 0
            && (char.IsLetter(segment[0]) || segment[0] == '_' || segment[0] == '$')
            && segment.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$');
    }

    /// <summary>
    /// Fix 1: Determines whether an async lambda has a void return type (→ runAsync vs supplyAsync).
    /// Uses the semantic model when available; defaults to supplyAsync (non-void) when not.
    /// </summary>
    private static bool IsVoidAsyncLambda(LambdaExpressionSyntax lambda, ConversionContext context)
    {
        if (context.SemanticModel == null) return false;
        var typeInfo = context.GetTypeInfo(lambda);
        if (typeInfo.ConvertedType is INamedTypeSymbol namedType)
        {
            var invokeMethod = namedType.DelegateInvokeMethod;
            if (invokeMethod != null)
                return invokeMethod.ReturnType.SpecialType == SpecialType.System_Void;
        }
        return false;
    }

    /// <summary>
    /// Detects whether a transformed expression string looks like an uncollected Java stream pipeline.
    /// Used to add .collect() when the lambda's target type expects Iterable/Collection.
    /// </summary>
    private static bool LooksLikeUncollectedStream(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        bool streamLike = expr.Contains(".stream(", StringComparison.Ordinal)
            || expr.Contains("StreamSupport.stream(", StringComparison.Ordinal)
            || expr.Contains("Arrays.stream(", StringComparison.Ordinal)
            || expr.Contains(".map(", StringComparison.Ordinal)
            || expr.Contains(".filter(", StringComparison.Ordinal)
            || expr.Contains(".sorted(", StringComparison.Ordinal)
            || expr.Contains(".flatMap(", StringComparison.Ordinal);
        if (!streamLike) return false;
        return !expr.Contains(".collect(", StringComparison.Ordinal)
            && !expr.EndsWith(".toList()", StringComparison.Ordinal);
    }

    /// <summary>
    /// Fix 3: Identifies local variables from enclosing scopes that are mutated inside the lambda body.
    /// Java requires captured variables to be effectively final; mutated ones must be wrapped in a
    /// single-element array holder to allow mutation via the array element.
    /// <para>
    /// KNOWN LIMITATION: This method only detects mutations INSIDE the lambda body. External
    /// reassignments (outside the lambda) are handled by <c>PreScanLambdaCaptures</c> in
    /// StatementTransformer. However, for-loop iteration variables (e.g. <c>i</c> in
    /// <c>for (int i=0; ...; i++)</c>) fall through both paths: pre-scan excludes them
    /// (pending holders can never be activated for for-loop variables), and this method
    /// does not detect <c>i++</c> as an internal mutation. This produces Java code that
    /// violates the effectively-final constraint for for-loop iteration variables.
    /// </para>
    /// </summary>
    private static IReadOnlyList<(string Name, string JavaType)> GetMutatedCaptures(
        LambdaExpressionSyntax lambda, ConversionContext context)
    {
        var result = new List<(string, string)>();
        if (context.SemanticModel == null) return result;

        // Collect lambda parameter names to exclude from capture detection
        var paramNames = lambda switch
        {
            SimpleLambdaExpressionSyntax s => new HashSet<string> { s.Parameter.Identifier.Text },
            ParenthesizedLambdaExpressionSyntax p =>
                new HashSet<string>(p.ParameterList.Parameters.Select(pm => pm.Identifier.Text)),
            _ => new HashSet<string>()
        };

        SyntaxNode? body = (SyntaxNode?)lambda.Block ?? lambda.ExpressionBody;
        if (body == null) return result;

        var seen = new HashSet<string>();

        foreach (var node in body.DescendantNodesAndSelf())
        {
            // Match assignment targets, ++ / -- operands, and ref/out parameter arguments.
            // ref/out arguments mutate the captured variable (the callee writes back),
            // so they must be treated as mutations for the purpose of holder replacement.
            IdentifierNameSyntax? targetId = node switch
            {
                AssignmentExpressionSyntax assign when assign.Left is IdentifierNameSyntax id => id,
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
            string name = targetId.Identifier.Text;
            if (paramNames.Contains(name) || seen.Contains(name)) continue;

            var symbol = context.GetSymbolInfo(targetId).Symbol;
            if (symbol is ILocalSymbol local)
            {
                // Confirm the declaration is outside the lambda span
                var declLocation = local.Locations.FirstOrDefault();
                if (declLocation != null && !lambda.Span.Contains(declLocation.SourceSpan))
                {
                    seen.Add(name);
                    string javaType = context.MapType(local.Type);
                    result.Add((name, javaType));
                }
            }
        }

        return result;
    }
}

