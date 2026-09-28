using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Statement;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Direct lambda mapping: C# lambdas become Java lambdas. Kept machinery:
/// (1) capture-holder rewriting for captured variables mutated inside the lambda;
/// (2) shadow renames (Java forbids lambda parameter/local names shadowing enclosing names).
/// Everything else (async, delegate {}, ref/out parameters, stream materialization,
/// implicit coercions) must be lowered by a preprocessor.
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
        => node switch
        {
            AnonymousMethodExpressionSyntax => throw new NotSupportedException(
                "CS2J normalization required: anonymous methods (`delegate { ... }`) must be rewritten to lambdas by a preprocessor before Java conversion."),
            LambdaExpressionSyntax lambda => TransformLambda(lambda, context),
            _ => throw new NotSupportedException($"Lambda expression kind {node.Kind()} not supported.")
        };

    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
        => new JavaRawExpression(Transform(node, context));

    private string TransformLambda(LambdaExpressionSyntax node, ConversionContext context)
    {
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
        {
            throw new NotSupportedException(
                "CS2J normalization required: async lambdas must be lowered (e.g. to CompletableFuture) by a preprocessor before Java conversion.");
        }

        var parameters = GetLambdaParameters(node);

        // Java forbids lambda parameters shadowing enclosing locals/parameters.
        var enclosingNames = CollectVisibleEnclosingNames(node);
        var paramRenames = new Dictionary<string, string>();
        var usedParamNames = new HashSet<string>();
        var javaParams = parameters
            .Select(p => BuildLambdaParameter(p, enclosingNames, paramRenames, usedParamNames, context))
            .ToList();
        var paramStr = FormatLambdaParameterList(javaParams);

        // Captured locals mutated inside the lambda need single-element array holders.
        var mutatedCaptures = GetMutatedCaptures(node, context);
        foreach (var (capName, capType) in mutatedCaptures)
        {
            if (context.MethodState.HasActiveLambdaCaptureHolder(capName))
                continue;

            var holderDecl = capType.Contains('<')
                ? $"@SuppressWarnings(\"unchecked\") {capType}[] _{capName} = ({capType}[]) new Object[] {{ {capName} }};"
                : $"{capType}[] _{capName} = new {capType}[] {{ {capName} }};";
            context.AddPreStatement(holderDecl);
            context.MethodState.RegisterActiveLambdaCaptureHolder(capName, $"_{capName}");
        }

        if (node.Block != null)
            return BuildBlockBodyLambda(node, paramStr, parameters, paramRenames, mutatedCaptures, context);

        if (node.ExpressionBody != null)
            return BuildExpressionBodyLambda(node, paramStr, paramRenames, context);

        return $"{paramStr} -> null";
    }

    private static string BuildLambdaParameter(
        ParameterSyntax p,
        HashSet<string> enclosingNames,
        Dictionary<string, string> paramRenames,
        HashSet<string> usedParamNames,
        ConversionContext context)
    {
        if (p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)))
        {
            throw new NotSupportedException(
                "CS2J normalization required: ref/out lambda parameters must be lowered (e.g. to holder objects) by a preprocessor before Java conversion.");
        }

        var name = ConversionContext.EscapeJavaKeyword(p.Identifier.Text);

        if (enclosingNames.Contains(p.Identifier.Text))
        {
            var newName = UniqueName(name, enclosingNames);
            paramRenames[p.Identifier.Text] = newName;
            name = newName;
        }
        if (usedParamNames.Contains(name))
            name = UniqueName(name, usedParamNames);
        usedParamNames.Add(name);

        if (p.Type != null)
        {
            var javaType = BoxPrimitive(context.MapTypeFromSyntax(p.Type));
            return $"{javaType} {name}";
        }
        return name;
    }

    private static string UniqueName(string baseName, HashSet<string> taken)
    {
        var candidate = $"{baseName}_1";
        var suffix = 2;
        while (taken.Contains(candidate))
            candidate = $"{baseName}_{suffix++}";
        taken.Add(candidate);
        return candidate;
    }

    private static string BoxPrimitive(string javaType)
        => javaType switch
        {
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "short" => "Short",
            "byte" => "Byte",
            "char" => "Character",
            "boolean" => "Boolean",
            _ => javaType
        };

    private string BuildBlockBodyLambda(
        LambdaExpressionSyntax node,
        string paramStr,
        IEnumerable<ParameterSyntax> parameters,
        Dictionary<string, string> paramRenames,
        IReadOnlyList<(string Name, string JavaType)> mutatedCaptures,
        ConversionContext context)
    {
        // Java also forbids body locals shadowing visible enclosing names.
        var bodyLocalBlockedNames = CollectVisibleEnclosingNames(node);
        foreach (var p in parameters) bodyLocalBlockedNames.Add(p.Identifier.Text);
        foreach (var newName in paramRenames.Values) bodyLocalBlockedNames.Add(newName);
        RegisterLambdaBodyShadowRenames(node.Block!, bodyLocalBlockedNames, context);

        // Holder pre-statements belong to the enclosing scope; keep them out of the body.
        var savedPre = context.HasPendingPreStatements ? context.DrainPreStatements().ToList() : new List<string>();
        var savedPost = context.HasPendingPostStatements ? context.DrainPostStatements().ToList() : new List<string>();

        var body = new StatementTransformer().TransformBlock(node.Block!, context);

        foreach (var ps in savedPre) context.AddPreStatement(ps);
        foreach (var ps in savedPost) context.AddPostStatement(ps);

        // Raw statement strings bypass IdentifierExpressionTransformer, so replace
        // mutated capture names and renamed parameters textually.
        foreach (var (capName, _) in mutatedCaptures)
            body = Regex.Replace(body, $@"\b{Regex.Escape(capName)}\b", $"_{capName}[0]");
        foreach (var (oldName, newName) in paramRenames)
            body = Regex.Replace(body, $@"\b{Regex.Escape(oldName)}\b", newName);

        return $"{paramStr} -> {{\n        {body}\n    }}";
    }

    private string BuildExpressionBodyLambda(
        LambdaExpressionSyntax node,
        string paramStr,
        Dictionary<string, string> paramRenames,
        ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        var savedPre = context.HasPendingPreStatements ? context.DrainPreStatements().ToList() : new List<string>();
        var savedPost = context.HasPendingPostStatements ? context.DrainPostStatements().ToList() : new List<string>();

        var body = facade.Transform(node.ExpressionBody!, context);

        var generatedInside = (context.HasPendingPreStatements ? context.DrainPreStatements().Count : 0)
                            + (context.HasPendingPostStatements ? context.DrainPostStatements().Count : 0);
        foreach (var ps in savedPre) context.AddPreStatement(ps);
        foreach (var ps in savedPost) context.AddPostStatement(ps);

        if (generatedInside > 0)
        {
            throw new NotSupportedException(
                "CS2J normalization required: lambda expression bodies containing ref/out argument write-backs or statement expansion must be lowered by a preprocessor before Java conversion.");
        }

        foreach (var (oldName, newName) in paramRenames)
            body = Regex.Replace(body, $@"\b{Regex.Escape(oldName)}\b", newName);

        return $"{paramStr} -> {body}";
    }

    private static string FormatLambdaParameterList(IReadOnlyList<string> javaParams)
    {
        return javaParams.Count == 1 && IsIdentifier(javaParams[0])
            ? javaParams[0]
            : $"({string.Join(", ", javaParams)})";
    }

    private static bool IsIdentifier(string text)
        => text.Length > 0
           && (char.IsLetter(text[0]) || text[0] is '_' or '$')
           && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');

    private static IEnumerable<ParameterSyntax> GetLambdaParameters(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => [simple.Parameter],
        ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters,
        _ => []
    };

    /// <summary>
    /// Local variables from enclosing scopes that are mutated inside the lambda body.
    /// Java requires captured variables to be effectively final; mutated ones are
    /// wrapped in a single-element array holder.
    /// </summary>
    private static IReadOnlyList<(string Name, string JavaType)> GetMutatedCaptures(
        LambdaExpressionSyntax lambda, ConversionContext context)
    {
        var result = new List<(string, string)>();
        if (context.SemanticModel == null) return result;

        var paramNames = new HashSet<string>(GetLambdaParameters(lambda).Select(p => p.Identifier.Text));
        SyntaxNode? body = lambda.Block ?? (SyntaxNode?)lambda.ExpressionBody;
        if (body == null) return result;

        var seen = new HashSet<string>();
        foreach (var node in body.DescendantNodesAndSelf())
        {
            IdentifierNameSyntax? targetId = node switch
            {
                AssignmentExpressionSyntax assign when assign.Left is IdentifierNameSyntax id => id,
                PrefixUnaryExpressionSyntax pre
                    when (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression))
                    && pre.Operand is IdentifierNameSyntax pid => pid,
                PostfixUnaryExpressionSyntax post
                    when (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression))
                    && post.Operand is IdentifierNameSyntax ppid => ppid,
                ArgumentSyntax arg
                    when (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                    && arg.Expression is IdentifierNameSyntax aid => aid,
                _ => null
            };

            if (targetId == null) continue;
            var name = targetId.Identifier.Text;
            if (paramNames.Contains(name) || !seen.Add(name)) continue;

            if (context.GetSymbolInfo(targetId).Symbol is ILocalSymbol local
                && local.Locations.FirstOrDefault() is { } declLocation
                && !lambda.Span.Contains(declLocation.SourceSpan))
            {
                result.Add((name, context.MapType(local.Type)));
            }
        }

        return result;
    }

    // ---- Shadow rename support -------------------------------------------------

    /// <summary>
    /// Names of parameters/locals visible from outside the given lambda node.
    /// Java forbids reusing any of these for lambda parameters or body locals.
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
                    break;
                case LambdaExpressionSyntax enclosingLambda:
                    AddParameterNames(GetLambdaParameters(enclosingLambda), names);
                    CollectLocalNamesExcludingScope(
                        enclosingLambda.Block ?? (SyntaxNode?)enclosingLambda.ExpressionBody, lambdaNode, names);
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

    private static void CollectLocalNamesExcludingScope(
        SyntaxNode? scope, SyntaxNode excludedNode, HashSet<string> names)
    {
        if (scope == null) return;
        foreach (var n in scope.DescendantNodes())
        {
            if (excludedNode.Span.Contains(n.Span)) continue;
            AddDeclaredName(n, names);
        }
    }

    private static void CollectVisibleLocalNames(
        SyntaxNode? scope, SyntaxNode lambdaNode, HashSet<string> names)
    {
        if (scope == null) return;
        foreach (var n in scope.DescendantNodes(
                     descendIntoChildren: c => c is not LambdaExpressionSyntax))
        {
            if (lambdaNode.Span.Contains(n.Span)) continue;
            if (!IsLocalVisibleTo(n, lambdaNode)) continue;
            AddDeclaredName(n, names);
        }
    }

    private static void AddDeclaredName(SyntaxNode n, HashSet<string> names)
    {
        switch (n)
        {
            case VariableDeclaratorSyntax vd when vd.Identifier.ValueText.Length > 0:
                names.Add(vd.Identifier.ValueText);
                break;
            case ForEachStatementSyntax fe when fe.Identifier.ValueText.Length > 0:
                names.Add(fe.Identifier.ValueText);
                break;
            case CatchDeclarationSyntax cd when cd.Identifier.ValueText.Length > 0:
                names.Add(cd.Identifier.ValueText);
                break;
            case SingleVariableDesignationSyntax svd:
                names.Add(svd.Identifier.ValueText);
                break;
        }
    }

    private static bool IsLocalVisibleTo(SyntaxNode declaration, SyntaxNode lambdaNode)
    {
        var current = declaration.Parent;
        while (current != null)
        {
            if (current is BlockSyntax block && !block.Contains(lambdaNode))
                return false;
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

    private static void RegisterLambdaBodyShadowRenames(
        BlockSyntax block, HashSet<string> blockedNames, ConversionContext context)
    {
        foreach (var n in block.DescendantNodes(
                     descendIntoChildren: n => n is not LambdaExpressionSyntax))
        {
            if (n is VariableDeclaratorSyntax or ForEachStatementSyntax
                or SingleVariableDesignationSyntax or CatchDeclarationSyntax)
            {
                TryRenameLambdaShadowedLocal(n, blockedNames, context);
            }
        }
    }

    private static void TryRenameLambdaShadowedLocal(
        SyntaxNode declaration, HashSet<string> blockedNames, ConversionContext context)
    {
        if (context.GetDeclaredSymbol(declaration) is not ILocalSymbol symbol) return;
        if (!blockedNames.Contains(symbol.Name)) return;
        if (context.MethodState.TryGetLocalRename(symbol, out _)) return;

        var newName = UniqueName(symbol.Name, blockedNames);
        context.MethodState.RegisterLocalRename(symbol, newName);
    }
}
