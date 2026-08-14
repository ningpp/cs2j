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
                    // ref/out lambda parameters need ObjectHolder wrapping which the
                    // structured-IR fast path does not model — fall back to raw transform.
                    IEnumerable<ParameterSyntax> parameters = lambda switch
                    {
                        SimpleLambdaExpressionSyntax simple => [simple.Parameter],
                        ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters,
                        _ => []
                    };
                    if (parameters.Any(p => p.Modifiers.Any(m =>
                            m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword))))
                    {
                        return new JavaRawExpression(Transform(node, context));
                    }

                    // Collect parameters
                    IEnumerable<ParameterSyntax> collectedParams = lambda switch
                    {
                        SimpleLambdaExpressionSyntax simple => [simple.Parameter],
                        ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters,
                        _ => []
                    };

                    var javaParams = collectedParams
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

        // C# lambda parameters may shadow enclosing locals (C# allows it); Java forbids a
        // lambda parameter with the same name as an enclosing local/parameter. Ref/out
        // parameters also need ObjectHolder<T> wrapping to match the generated delegate
        // signature. Track renames so the lambda body can be rewritten consistently.
        var enclosingNames = CollectEnclosingMethodNames(node);
        var paramRenames = new Dictionary<string, string>();
        var javaParams = parameters
            .Select(p =>
            {
                string name = ConversionContext.EscapeJavaKeyword(p.Identifier.Text);
                bool isRefOrOut = p.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword));
                // C# lambda parameters may shadow enclosing locals (C# allows it); Java
                // forbids a lambda parameter with the same name as an enclosing local or
                // parameter. Apply the rename regardless of whether the parameter is
                // explicitly typed (simple lambdas like `m -> ...` can shadow too).
                if (enclosingNames.Contains(p.Identifier.Text))
                {
                    string newName = $"{name}_1";
                    int suffix = 2;
                    while (enclosingNames.Contains(newName))
                    {
                        newName = $"{name}_{suffix}";
                        suffix++;
                    }
                    paramRenames[p.Identifier.Text] = newName;
                    name = newName;
                }
                // Fix 2: emit explicit type when the parameter carries a declared type
                if (p.Type != null)
                {
                    string javaType = ExpressionTransformerHelpers.BoxJavaPrimitiveType(
                        context.MapTypeFromSyntax(p.Type));
                    if (isRefOrOut)
                    {
                        context.AddImport("io.github.ningpp.compat.ObjectHolder");
                        javaType = $"io.github.ningpp.compat.ObjectHolder<{javaType}>";
                    }
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
            // C# allows a local declared inside the lambda body to shadow a variable
            // visible from the enclosing scope (method parameter/local or enclosing
            // lambda parameter/local); Java forbids that shadowing, so the inner
            // declarations (and every reference that binds to them) must be renamed.
            // Register the renames before the body is transformed so that both the
            // declaration emission and the reference emission use the new names.
            var bodyLocalBlockedNames = CollectVisibleEnclosingNames(node);
            foreach (var p in parameters) bodyLocalBlockedNames.Add(p.Identifier.Text);
            foreach (var newName in paramRenames.Values) bodyLocalBlockedNames.Add(newName);
            RegisterLambdaBodyShadowRenames(node.Block, bodyLocalBlockedNames, context);

            // Save pending pre-statements before processing the block body.
            // Array-holder pre-statements added above must be emitted at the
            // enclosing scope (before the statement containing this lambda),
            // not inside the lambda body. TransformBlock drains all pending
            // pre-statements, so we preserve and restore them here.
            var savedPre = context.HasPendingPreStatements
                ? context.DrainPreStatements().ToList()
                : new List<string>();
            // Same for post-statements: ref/out write-backs generated while converting
            // the invocation's other arguments (e.g. a `ref state` argument preceding the
            // lambda) must stay at the enclosing call site, not leak into the lambda body.
            var savedPost = context.HasPendingPostStatements
                ? context.DrainPostStatements().ToList()
                : new List<string>();

            var stmtTransformer = new StatementTransformer();
            // Non-void async lambdas are wrapped in supplyAsync and must return the
            // CompletableFuture; terminal `await X;` statements become `return X;`.
            bool asyncReturnBody = isAsync && !IsVoidAsyncLambda(node, context);
            context.MethodState.InAsyncLambdaReturnPosition = asyncReturnBody;
            string body;
            try
            {
                body = stmtTransformer.TransformBlock(node.Block, context);
            }
            finally
            {
                context.MethodState.InAsyncLambdaReturnPosition = false;
            }

            // Restore saved pre-statements for the enclosing scope
            foreach (var ps in savedPre)
                context.AddPreStatement(ps);
            foreach (var ps in savedPost)
                context.AddPostStatement(ps);

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

            // Apply parameter renames (shadowing/conflict avoidance) to the lambda body.
            foreach (var (oldName, newName) in paramRenames)
                body = Regex.Replace(body, $@"\b{Regex.Escape(oldName)}\b", newName);

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

            // Apply parameter renames (shadowing/conflict avoidance) to the lambda body.
            foreach (var (oldName, newName) in paramRenames)
                body = Regex.Replace(body, $@"\b{Regex.Escape(oldName)}\b", newName);

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
                    {
                        // Java's var cannot infer a type from a null initializer. When the
                        // body value is null (null literal or a cast of null, e.g. from a
                        // hoisted property assignment `obj.Name = null`), declare __result
                        // with the mapped type of the expression body instead of var.
                        string resultDecl = EmitsNullValue(body)
                            ? GetLambdaBodyResultType(node, context)
                            : "var";
                        result = $"{paramStr} -> {{\n{preBlock}\n{resultDecl} __result = {body};\n{postBlock}\nreturn __result;\n}}";
                    }
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

    /// <summary>
    /// True when the transformed body string is a Java null value (the null literal
    /// or a cast of null), which Java's var cannot infer a type from.
    /// </summary>
    private static bool EmitsNullValue(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        var trimmed = body.Trim();
        if (trimmed == "null")
            return true;
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\(.+\)\s*null$");
    }

    /// <summary>
    /// Returns the Java type to declare the lambda's __result variable with when
    /// the body value is null. Uses the C# type of the expression body via the
    /// semantic model; falls back to Object when it cannot be resolved.
    /// </summary>
    private static string GetLambdaBodyResultType(LambdaExpressionSyntax lambda, ConversionContext context)
    {
        if (lambda.ExpressionBody != null && context.SemanticModel != null)
        {
            var typeInfo = context.GetTypeInfo(lambda.ExpressionBody);
            if (typeInfo.Type is { } typeSymbol
                && typeSymbol is not IErrorTypeSymbol)
            {
                var javaType = context.MapType(typeSymbol);
                if (!string.IsNullOrWhiteSpace(javaType) && javaType != "var")
                    return javaType;
            }
        }
        return "Object";
    }

    private string TransformAnonymousMethod(AnonymousMethodExpressionSyntax node, ConversionContext context)
    {
        var stmtTransformer = new StatementTransformer();

        // Anonymous method bodies have the same C#-allows / Java-forbids shadowing
        // rules as lambda bodies - rename body locals that collide with enclosing
        // scope names before the body is transformed.
        var bodyLocalBlockedNames = CollectVisibleEnclosingNames(node);
        if (node.ParameterList != null)
        {
            foreach (var p in node.ParameterList.Parameters)
                bodyLocalBlockedNames.Add(p.Identifier.Text);
        }

        // Fix 5: C# allows `delegate { ... }` (no parameter list) to be converted to any
        // delegate type, e.g. `(RoutedEventHandler)(delegate { ... })`. Java lambdas must
        // declare the parameters, so synthesize names from the target delegate's invoke
        // signature when the anonymous method omits its parameter list.
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
            paramStr = SynthesizeAnonymousMethodParameterList(node, bodyLocalBlockedNames, context);
        }
        RegisterLambdaBodyShadowRenames(node.Block, bodyLocalBlockedNames, context);

        var body = stmtTransformer.TransformBlock(node.Block, context);
        return $"{paramStr} -> {{\n{body}\n}}";
    }

    /// <summary>
    /// Builds a Java lambda parameter list for a parameterless C# anonymous method by
    /// reading the target delegate type's invoke signature (via the semantic model or
    /// an enclosing cast expression). Falls back to () when no delegate target can be
    /// resolved.
    /// </summary>
    private static string SynthesizeAnonymousMethodParameterList(
        AnonymousMethodExpressionSyntax node,
        HashSet<string> blockedNames,
        ConversionContext context)
    {
        var delegateType = TryResolveTargetDelegateType(node, context);
        var invoke = GetSamInvokeMethod(delegateType);
        if (invoke != null)
            return BuildSynthesizedParameterList(invoke.Parameters.Length, blockedNames);

        // Semantic resolution failed (e.g. the cast type is an unresolved/error type).
        // Fall back to reading the mapped compat functional interface's signature so
        // `(RoutedEventHandler)(delegate { ... })` still gets the right arity.
        var compatCount = TryGetCompatSamParameterCount(node, context);
        if (compatCount.HasValue)
            return BuildSynthesizedParameterList(compatCount.Value, blockedNames);

        WriteLambdaResolutionDiagnostic(node, context, delegateType);
        return "()";
    }

    private static string BuildSynthesizedParameterList(int parameterCount, HashSet<string> blockedNames)
    {
        if (parameterCount <= 0)
            return "()";

        var used = new HashSet<string>(blockedNames, StringComparer.Ordinal);
        var names = new List<string>();
        var defaultNames = new[] { "sender", "e", "arg0", "arg1", "arg2", "arg3", "arg4", "arg5" };
        for (int i = 0; i < parameterCount; i++)
        {
            var candidate = i < defaultNames.Length ? defaultNames[i] : $"arg{i}";
            while (used.Contains(candidate))
                candidate = "_" + candidate;
            used.Add(candidate);
            names.Add(candidate);
            blockedNames.Add(candidate);
        }
        return $"({string.Join(", ", names)})";
    }

    /// <summary>
    /// Returns the invoke/SAM method of a target delegate-like type. Supports both
    /// real delegates (DelegateInvokeMethod) and functional-interface style compat
    /// types (e.g. dotnet.wpf.swing.RoutedEventHandler whose SAM is accept(...)).
    /// </summary>
    private static IMethodSymbol? GetSamInvokeMethod(INamedTypeSymbol? type)
    {
        if (type == null)
            return null;

        if (type.DelegateInvokeMethod != null)
            return type.DelegateInvokeMethod;

        return type.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => !m.IsStatic
                && m.MethodKind == MethodKind.Ordinary
                && (m.IsAbstract || !m.IsSealed));
    }

    private static INamedTypeSymbol? TryResolveTargetDelegateType(
        AnonymousMethodExpressionSyntax node,
        ConversionContext context)
    {
        if (context.SemanticModel == null)
            return null;

        var typeInfo = context.GetTypeInfo(node);
        var type = typeInfo.ConvertedType ?? typeInfo.Type;
        if (type is INamedTypeSymbol named && IsDelegateLike(named))
            return named;

        // The cast may be separated from the anonymous method by parentheses:
        // (RoutedEventHandler)(delegate { ... })
        var cast = FindEnclosingCast(node);
        if (cast != null)
        {
            var castType = context.GetTypeInfo(cast).Type;
            if (castType is INamedTypeSymbol castNamed && IsDelegateLike(castNamed))
                return castNamed;
        }

        return null;
    }

    private static bool IsDelegateLike(INamedTypeSymbol type)
        => type.TypeKind == TypeKind.Delegate || type.TypeKind == TypeKind.Interface;

    private static CastExpressionSyntax? FindEnclosingCast(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            if (current is CastExpressionSyntax cast)
                return cast;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// When the semantic model cannot resolve the cast target (TypeKind.Error), locate
    /// the mapped compat functional interface in the csharptojava-compat source tree and
    /// count its SAM parameters. Returns null when no compat interface can be found.
    /// </summary>
    private static int? TryGetCompatSamParameterCount(
        AnonymousMethodExpressionSyntax node,
        ConversionContext context)
    {
        var cast = FindEnclosingCast(node);
        if (cast?.Type == null)
            return null;

        var compatRoot = CompatSourceRoot;
        if (compatRoot == null)
            return null;

        var simpleName = cast.Type.ToString().Split('.')[^1].Split('<')[0].Trim();
        if (string.IsNullOrWhiteSpace(simpleName)
            || simpleName is "Object" or "?" or "var")
            return null;

        try
        {
            var file = Directory.EnumerateFiles(compatRoot, simpleName + ".java", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (file == null)
                return null;

            var text = File.ReadAllText(file);
            var count = CountSamParameters(text, simpleName);
            if (count.HasValue)
                return count;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static readonly Lazy<string?> _compatSourceRoot = new(ResolveCompatSourceRoot);

    /// <summary>
    /// Locates the csharptojava-compat Java source root. Honors CS2J_COMPAT_SOURCE,
    /// then searches upward from the CLI binary directory for java/csharptojava-compat.
    /// </summary>
    private static string? CompatSourceRoot => _compatSourceRoot.Value;

    private static string? ResolveCompatSourceRoot()
    {
        var env = Environment.GetEnvironmentVariable("CS2J_COMPAT_SOURCE");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "java", "csharptojava-compat", "src", "main", "java");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Counts the parameters of the single abstract method of a compat interface.
    /// Returns null when no SAM can be identified.
    /// </summary>
    private static int? CountSamParameters(string text, string simpleName)
    {
        var interfaceIdx = text.IndexOf("interface " + simpleName, StringComparison.Ordinal);
        if (interfaceIdx < 0)
        {
            interfaceIdx = text.LastIndexOf(simpleName, StringComparison.Ordinal);
            if (interfaceIdx < 0)
                return null;
        }
        var openBrace = text.IndexOf('{', interfaceIdx);
        if (openBrace < 0)
            return null;
        var closeBrace = text.LastIndexOf('}');
        if (closeBrace <= openBrace)
            return null;

        var body = text.Substring(openBrace + 1, closeBrace - openBrace - 1);
        foreach (Match match in Regex.Matches(body, @"[\w$<>,\s\[\]]+\s+[\w$]+\s*\(([^)]*)\)\s*;"))
        {
            var prefix = body.Substring(Math.Max(0, match.Index - 24), Math.Min(24, match.Index));
            if (prefix.Contains("default", StringComparison.Ordinal)
                || prefix.Contains("static", StringComparison.Ordinal))
                continue;

            var parameters = match.Groups[1].Value.Trim();
            if (parameters.Length == 0)
                return 0;
            return CountTopLevelArgumentSeparators(parameters) + 1;
        }
        return null;
    }

    private static int CountTopLevelArgumentSeparators(string parameters)
    {
        int depth = 0;
        int separators = 0;
        foreach (char c in parameters)
        {
            if (c is '<' or '(' or '[')
                depth++;
            else if (c is '>' or ')' or ']')
                depth--;
            else if (c == ',' && depth == 0)
                separators++;
        }
        return separators;
    }

    /// <summary>
    /// Writes lambda-target resolution details to the file given by CS2J_LAMBDA_DIAG
    /// (used for diagnosing unresolved anonymous-method targets in the field).
    /// </summary>
    private static void WriteLambdaResolutionDiagnostic(
        AnonymousMethodExpressionSyntax node,
        ConversionContext context,
        INamedTypeSymbol? delegateType)
    {
        var path = Environment.GetEnvironmentVariable("CS2J_LAMBDA_DIAG");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"node: {node.ToString().Replace('\n', ' ')}");
            sb.AppendLine($"semanticModel: {(context.SemanticModel != null ? "yes" : "no")}");
            if (context.SemanticModel != null)
            {
                var typeInfo = context.GetTypeInfo(node);
                sb.AppendLine($"typeInfo.Type: {typeInfo.Type} (kind: {typeInfo.Type?.TypeKind})");
                sb.AppendLine($"typeInfo.ConvertedType: {typeInfo.ConvertedType} (kind: {typeInfo.ConvertedType?.TypeKind})");
            }
            sb.AppendLine($"resolvedDelegateType: {delegateType} (kind: {delegateType?.TypeKind})");
            var cast = FindEnclosingCast(node);
            if (cast != null)
            {
                sb.AppendLine($"castTypeSyntax: {cast.Type}");
                if (context.SemanticModel != null)
                {
                    var castType = context.GetTypeInfo(cast).Type;
                    sb.AppendLine($"castType: {castType} (kind: {castType?.TypeKind})");
                }
            }
            File.AppendAllText(path, sb.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
    /// Collects local-variable and parameter names declared in the enclosing method
    /// (and in all nested blocks between the method body and the lambda). Java forbids
    /// a lambda parameter with the same name as any enclosing local/parameter, while
    /// C# allows shadowing — so conflicting parameters must be renamed.
    /// </summary>
    private static HashSet<string> CollectEnclosingMethodNames(LambdaExpressionSyntax lambda)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        SyntaxNode? current = lambda.Parent;
        while (current != null)
        {
            if (current is LambdaExpressionSyntax enclosingLambda)
            {
                foreach (var p in GetLambdaParameters(enclosingLambda))
                    names.Add(p.Identifier.Text);
                if (enclosingLambda.Block != null)
                    CollectLocalNames(enclosingLambda.Block, names);
                else if (enclosingLambda.ExpressionBody != null)
                    CollectLocalNames(enclosingLambda.ExpressionBody, names);
                // Do not break — enclosing lambda parameters/locals are visible too,
                // and the enclosing method's names must still be collected.
            }
            else if (current is AnonymousMethodExpressionSyntax enclosingAnon)
            {
                if (enclosingAnon.ParameterList != null)
                {
                    foreach (var p in enclosingAnon.ParameterList.Parameters)
                        names.Add(p.Identifier.Text);
                }
                CollectLocalNames(enclosingAnon.Block, names);
            }
            else if (current is MethodDeclarationSyntax method)
            {
                foreach (var p in method.ParameterList.Parameters)
                    names.Add(p.Identifier.Text);
                if (method.Body != null)
                    CollectLocalNames(method.Body, names);
                if (method.ExpressionBody != null)
                    CollectLocalNames(method.ExpressionBody, names);
                break;
            }
            else if (current is LocalFunctionStatementSyntax localFn)
            {
                foreach (var p in localFn.ParameterList.Parameters)
                    names.Add(p.Identifier.Text);
                if (localFn.Body != null)
                    CollectLocalNames(localFn.Body, names);
                break;
            }
            else if (current is AccessorDeclarationSyntax accessor)
            {
                if (accessor.Body != null)
                    CollectLocalNames(accessor.Body, names);
                break;
            }
            else if (current is ArrowExpressionClauseSyntax arrow)
            {
                CollectLocalNames(arrow, names);
                break;
            }
            current = current.Parent;
        }
        return names;
    }

    /// <summary>
    /// Collects the names of every variable/parameter visible from outside
    /// <paramref name="lambdaNode"/>: parameters of the enclosing method/constructor/
    /// accessor/local function, locals declared in their bodies outside the lambda's
    /// subtree, and parameters/locals of enclosing lambdas. Java forbids a local
    /// declared inside the lambda body from reusing any of these names, while C#
    /// allows the shadowing — so such declarations must be renamed.
    /// </summary>
    internal static HashSet<string> CollectVisibleEnclosingNames(SyntaxNode lambdaNode)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        SyntaxNode? current = lambdaNode.Parent;
        while (current != null)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    AddParameterNames(method.ParameterList.Parameters, names);
                    CollectVisibleLocalNames(method.Body ?? (SyntaxNode?)method.ExpressionBody, lambdaNode, names);
                    return names;
                case ConstructorDeclarationSyntax ctor:
                    AddParameterNames(ctor.ParameterList.Parameters, names);
                    CollectVisibleLocalNames(ctor.Body ?? (SyntaxNode?)ctor.ExpressionBody, lambdaNode, names);
                    return names;
                case OperatorDeclarationSyntax op:
                    AddParameterNames(op.ParameterList.Parameters, names);
                    CollectVisibleLocalNames(op.Body ?? (SyntaxNode?)op.ExpressionBody, lambdaNode, names);
                    return names;
                case ConversionOperatorDeclarationSyntax conv:
                    AddParameterNames(conv.ParameterList.Parameters, names);
                    CollectVisibleLocalNames(conv.Body ?? (SyntaxNode?)conv.ExpressionBody, lambdaNode, names);
                    return names;
                case DestructorDeclarationSyntax destructor:
                    CollectVisibleLocalNames(destructor.Body, lambdaNode, names);
                    return names;
                case IndexerDeclarationSyntax indexer:
                    AddParameterNames(indexer.ParameterList.Parameters, names);
                    // Accessor bodies are visited as AccessorDeclarationSyntax.
                    CollectVisibleLocalNames(indexer.ExpressionBody, lambdaNode, names);
                    break;
                case LocalFunctionStatementSyntax localFn:
                    AddParameterNames(localFn.ParameterList.Parameters, names);
                    CollectVisibleLocalNames(localFn.Body ?? (SyntaxNode?)localFn.ExpressionBody, lambdaNode, names);
                    return names;
                case AccessorDeclarationSyntax accessor:
                    CollectVisibleLocalNames(accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody, lambdaNode, names);
                    return names;
                case ArrowExpressionClauseSyntax:
                    // Transparent — the owning member contributes its names above/below.
                    break;
                case LambdaExpressionSyntax enclosingLambda:
                    AddParameterNames(GetLambdaParameters(enclosingLambda), names);
                    CollectLocalNamesExcludingScope(enclosingLambda.Block ?? (SyntaxNode?)enclosingLambda.ExpressionBody, lambdaNode, names);
                    break; // continue to the enclosing method — its names are visible too
                case AnonymousMethodExpressionSyntax enclosingAnon:
                    if (enclosingAnon.ParameterList != null)
                        AddParameterNames(enclosingAnon.ParameterList.Parameters, names);
                    CollectLocalNamesExcludingScope(enclosingAnon.Block, lambdaNode, names);
                    break;
            }
            current = current.Parent;
        }
        return names;
    }

    private static void AddParameterNames(IEnumerable<ParameterSyntax> parameters, HashSet<string> names)
    {
        foreach (var p in parameters)
            names.Add(p.Identifier.Text);
    }

    private static IEnumerable<ParameterSyntax> GetLambdaParameters(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => [simple.Parameter],
        ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters,
        _ => []
    };

    /// <summary>
    /// Like <see cref="CollectLocalNames"/>, but skips declarations whose span lies
    /// inside <paramref name="excludedNode"/> (the lambda being processed), because
    /// those are not visible from the lambda.
    /// </summary>
    private static void CollectLocalNamesExcludingScope(
        SyntaxNode? scope, SyntaxNode excludedNode, HashSet<string> names)
    {
        if (scope == null) return;
        foreach (var n in scope.DescendantNodes())
        {
            if (excludedNode.Span.Contains(n.Span)) continue;
            switch (n)
            {
                case VariableDeclarationSyntax vd:
                    foreach (var v in vd.Variables)
                        if (v.Identifier.ValueText.Length > 0)
                            names.Add(v.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax fe:
                    if (fe.Identifier.ValueText.Length > 0)
                        names.Add(fe.Identifier.ValueText);
                    break;
                case CatchDeclarationSyntax cd:
                    if (cd.Identifier.ValueText.Length > 0)
                        names.Add(cd.Identifier.ValueText);
                    break;
                case SingleVariableDesignationSyntax svd:
                    names.Add(svd.Identifier.ValueText);
                    break;
            }
        }
    }

    /// <summary>
    /// Collects local names declared in <paramref name="scope"/> that are actually
    /// visible at <paramref name="lambdaNode"/>. Locals inside sibling blocks (which
    /// do not contain the lambda) are NOT visible in C# and must not force a rename
    /// of the lambda's own locals.
    /// </summary>
    private static void CollectVisibleLocalNames(
        SyntaxNode? scope, SyntaxNode lambdaNode, HashSet<string> names)
    {
        if (scope == null) return;
        foreach (var n in scope.DescendantNodes(
                     descendIntoChildren: c =>
                         c is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)))
        {
            if (lambdaNode.Span.Contains(n.Span)) continue;
            if (!IsLocalVisibleTo(n, lambdaNode)) continue;
            switch (n)
            {
                case VariableDeclarationSyntax vd:
                    foreach (var v in vd.Variables)
                        if (v.Identifier.ValueText.Length > 0)
                            names.Add(v.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax fe:
                    if (fe.Identifier.ValueText.Length > 0)
                        names.Add(fe.Identifier.ValueText);
                    break;
                case CatchDeclarationSyntax cd:
                    if (cd.Identifier.ValueText.Length > 0)
                        names.Add(cd.Identifier.ValueText);
                    break;
                case SingleVariableDesignationSyntax svd:
                    names.Add(svd.Identifier.ValueText);
                    break;
            }
        }
    }

    private static bool IsLocalVisibleTo(SyntaxNode declaration, SyntaxNode lambdaNode)
    {
        var current = declaration.Parent;
        while (current != null)
        {
            if (current is BlockSyntax block)
            {
                if (!block.Contains(lambdaNode))
                    return false;
            }
            if (current is MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or LocalFunctionStatementSyntax
                or LambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax)
            {
                break;
            }
            current = current.Parent;
        }
        return true;
    }

    /// <summary>
    /// Finds every local declared in <paramref name="block"/> whose name collides with
    /// a name in <paramref name="blockedNames"/> (names visible from outside the lambda)
    /// and registers a rename for it. Declarations inside nested lambdas are skipped —
    /// each lambda registers its own renames when it is transformed.
    /// </summary>
    private static void RegisterLambdaBodyShadowRenames(
        BlockSyntax block, HashSet<string> blockedNames, ConversionContext context)
    {
        foreach (var n in block.DescendantNodes(
                     descendIntoChildren: n => n is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)))
        {
            switch (n)
            {
                case VariableDeclaratorSyntax vd when vd.Identifier.ValueText.Length > 0:
                    TryRenameLambdaShadowedLocal(vd, blockedNames, context);
                    break;
                case ForEachStatementSyntax fe when fe.Identifier.ValueText.Length > 0:
                    TryRenameLambdaShadowedLocal(fe, blockedNames, context);
                    break;
                case SingleVariableDesignationSyntax svd:
                    TryRenameLambdaShadowedLocal(svd, blockedNames, context);
                    break;
                case CatchDeclarationSyntax cd when cd.Identifier.ValueText.Length > 0:
                    TryRenameLambdaShadowedLocal(cd, blockedNames, context);
                    break;
            }
        }
    }

    private static void TryRenameLambdaShadowedLocal(
        SyntaxNode declaration, HashSet<string> blockedNames, ConversionContext context)
    {
        var symbol = context.GetDeclaredSymbol(declaration) as ILocalSymbol;
        if (symbol == null) return;
        if (!blockedNames.Contains(symbol.Name)) return;
        if (context.MethodState.TryGetLocalRename(symbol, out _)) return;

        string newName = $"{symbol.Name}_1";
        int suffix = 2;
        while (blockedNames.Contains(newName))
        {
            newName = $"{symbol.Name}_{suffix}";
            suffix++;
        }
        blockedNames.Add(newName);
        context.MethodState.RegisterLocalRename(symbol, newName);
    }

    private static void CollectLocalNames(SyntaxNode scope, HashSet<string> names)
    {
        // Local variable declarations
        foreach (var decl in scope.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            foreach (var v in decl.Variables)
                names.Add(v.Identifier.Text);
        }
        // Foreach iteration variables
        foreach (var fe in scope.DescendantNodes().OfType<ForEachStatementSyntax>())
            names.Add(fe.Identifier.Text);
        // For-loop declared variables
        foreach (var forStmt in scope.DescendantNodes().OfType<ForStatementSyntax>())
        {
            if (forStmt.Declaration != null)
            {
                foreach (var v in forStmt.Declaration.Variables)
                    names.Add(v.Identifier.Text);
            }
        }
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
    /// This method only detects mutations INSIDE the lambda body. External reassignments
    /// (outside the lambda) are handled by <c>PreScanLambdaCaptures</c> in StatementTransformer,
    /// including for-loop iteration variables (e.g. <c>i</c> in <c>for (int i=0; ...; i++)</c>).
    /// The corresponding holder declaration for for-loop variables is emitted by
    /// <see cref="StatementTransformer.TransformForStatement"/>.
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
