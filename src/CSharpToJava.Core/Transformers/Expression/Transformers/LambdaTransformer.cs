using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
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
                                string javaType = context.MapTypeFromSyntax(p.Type);
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
                    string javaType = context.MapTypeFromSyntax(p.Type);
                    return $"{javaType} {name}";
                }
                return name;
            })
            .ToList();

        var paramStr = javaParams.Count == 1 ? javaParams[0] : $"({string.Join(", ", javaParams)})";

        // Fix 3: Detect mutated captured variables and add array-holder pre-statements
        var mutatedCaptures = GetMutatedCaptures(node, context);
        foreach (var (capName, capType) in mutatedCaptures)
        {
            context.AddPreStatement($"{capType}[] _{capName} = {{ {capName} }};");
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
            var stmtTransformer = new StatementTransformer();
            string body = stmtTransformer.TransformBlock(node.Block, context);

            // Fix 3: replace mutated captured variable usages with array-element access
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

            // Fix 3: replace mutated captured variable usages with array-element access
            foreach (var (capName, _) in mutatedCaptures)
            {
                body = Regex.Replace(body, $@"\b{Regex.Escape(capName)}\b", $"_{capName}[0]");
                body = Regex.Replace(
                    body,
                    $@"(_{Regex.Escape(capName)}\s*=\s*\{{\s*)_{Regex.Escape(capName)}\[0\](\s*\}})",
                    $"$1{capName}$2");
                for (int i = 0; i < preStatements.Count; i++)
                {
                    preStatements[i] = Regex.Replace(preStatements[i], $@"\b{Regex.Escape(capName)}\b", $"_{capName}[0]");
                    preStatements[i] = Regex.Replace(
                        preStatements[i],
                        $@"(_{Regex.Escape(capName)}\s*=\s*\{{\s*)_{Regex.Escape(capName)}\[0\](\s*\}})",
                        $"$1{capName}$2");
                }
            }

            string result;

            // Fix 6: Pre-statements don't have semicolons - add them when embedding in block
            var preStatementsWithSemis = preStatements.Select(s =>
                string.IsNullOrWhiteSpace(s) ? s : (s.EndsWith(";") ? s : s + ";")).ToList();

            // Fix 1: wrap async expression-body lambdas in CompletableFuture
            if (isAsync)
            {
                context.AddImport("java.util.concurrent.CompletableFuture");
                bool isVoid = IsVoidAsyncLambda(node, context);
                string asyncBody = preStatements.Count > 0
                    ? $"{{\n{string.Join("\n", preStatementsWithSemis)}\nreturn {body};\n}}"
                    : body;
                result = isVoid
                    ? $"CompletableFuture.runAsync(() -> {asyncBody})"
                    : $"CompletableFuture.supplyAsync(() -> {asyncBody})";
            }
            else
            {
                // If there are pre-statements, convert to block body
                if (preStatements.Count > 0)
                {
                    var blockBody = string.Join("\n", preStatementsWithSemis);
                    result = $"{paramStr} -> {{\n{blockBody}\nreturn {body};\n}}";
                }
                else
                {
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

    /// <summary>
    /// Fix 1: Determines whether an async lambda has a void return type (→ runAsync vs supplyAsync).
    /// Uses the semantic model when available; defaults to supplyAsync (non-void) when not.
    /// </summary>
    private static bool IsVoidAsyncLambda(LambdaExpressionSyntax lambda, ConversionContext context)
    {
        if (context.SemanticModel == null) return false;
        var typeInfo = context.SemanticModel.GetTypeInfo(lambda);
        if (typeInfo.ConvertedType is INamedTypeSymbol namedType)
        {
            var invokeMethod = namedType.DelegateInvokeMethod;
            if (invokeMethod != null)
                return invokeMethod.ReturnType.SpecialType == SpecialType.System_Void;
        }
        return false;
    }

    /// <summary>
    /// Fix 3: Identifies local variables from enclosing scopes that are mutated inside the lambda body.
    /// Java requires captured variables to be effectively final; mutated ones must be wrapped in a
    /// single-element array holder to allow mutation via the array element.
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
            // Match assignment targets and ++ / -- operands
            IdentifierNameSyntax? targetId = node switch
            {
                AssignmentExpressionSyntax assign when assign.Left is IdentifierNameSyntax id => id,
                PrefixUnaryExpressionSyntax pre when
                    (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression)) &&
                    pre.Operand is IdentifierNameSyntax pid => pid,
                PostfixUnaryExpressionSyntax post when
                    (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression)) &&
                    post.Operand is IdentifierNameSyntax ppid => ppid,
                _ => null
            };

            if (targetId == null) continue;
            string name = targetId.Identifier.Text;
            if (paramNames.Contains(name) || seen.Contains(name)) continue;

            var symbol = context.SemanticModel.GetSymbolInfo(targetId).Symbol;
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

