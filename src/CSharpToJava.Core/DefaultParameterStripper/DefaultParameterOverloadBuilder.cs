using CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// Constructs forwarding overloads from a stripped original.
///
/// For every parameter position that owns a default value, an overload is
/// emitted that keeps parameters [0..cutAt) and forwards the remaining ones with
/// their verbatim default expression.
///
/// Example: <c>Add(int a = 1, int b = 2, int c = 3)</c> yields
/// <code>
///   Add()             => Add(1, 2, 3)
///   Add(int a)        => Add(a, 2, 3)
///   Add(int a, int b) => Add(a, b, 3)
/// </code>
/// while the original keeps all three parameters with the defaults removed.
///
/// Overload generation deliberately does NOT try to guess whether two same-arity
/// overloads "look ambiguous": different parameter types are perfectly legal C#
/// and rejecting them silently breaks call sites. Only an exact duplicate of an
/// existing signature is rejected.
/// </summary>
internal static class DefaultParameterOverloadBuilder
{
    public static List<MethodDeclarationSyntax> BuildOverloads(
        MethodDeclarationSyntax method, SemanticModel model,
        DefaultParameterStripperStatistics stats,
        DefaultParameterCatalog? catalog = null,
        ParamsOverloadContext? paramsContext = null)
    {
        var result = new List<MethodDeclarationSyntax>();
        var parameters = method.ParameterList.Parameters;
        var cutPoints = CollectCutPoints(parameters);
        if (cutPoints.Count == 0) return result;

        var isVoid = IsVoidType(method.ReturnType);
        var filteredModifiers = FilterModifiers(method.Modifiers);
        var containingSymbol = model.GetDeclaredSymbol(method)?.ContainingType;

        foreach (var cutAt in cutPoints)
        {
            if (catalog != null && containingSymbol != null
                && HasConflictingSignature(catalog, model, containingSymbol, method.Identifier.Text, parameters, cutAt))
            {
                stats.SkippedConflict++;
                continue;
            }

            var retained = BuildRetainedParams(parameters, cutAt);
            var args = BuildForwardingArgs(parameters, cutAt, model, paramsContext);

            var overload = SyntaxFactory.MethodDeclaration(method.ReturnType, method.Identifier)
                .WithModifiers(filteredModifiers)
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(retained)))
                .WithTypeParameterList(method.TypeParameterList)
                .WithConstraintClauses(method.ConstraintClauses)
                .WithBody(SyntaxFactory.Block(
                    BuildForwardingStatement(BuildInvocationTarget(method), args, isVoid)))
                .WithoutDeclarationLeadingTrivia();

            result.Add(overload);
            stats.OverloadsGenerated++;
        }

        return result;
    }

    public static List<ConstructorDeclarationSyntax> BuildOverloads(
        ConstructorDeclarationSyntax ctor, SemanticModel model,
        DefaultParameterStripperStatistics stats,
        bool isStruct = false, DefaultParameterCatalog? catalog = null,
        ParamsOverloadContext? paramsContext = null)
    {
        var result = new List<ConstructorDeclarationSyntax>();
        var parameters = ctor.ParameterList.Parameters;
        var cutPoints = CollectCutPoints(parameters);
        if (cutPoints.Count == 0) return result;

        var containingSymbol = model.GetDeclaredSymbol(ctor)?.ContainingType;

        foreach (var cutAt in cutPoints)
        {
            // Never synthesise a parameterless constructor. The original type had
            // none (a constructor whose parameters are all optional still takes
            // those parameters), and adding one changes how reflection-driven
            // consumers — serializers, activators, DI containers — discover and
            // use the type. Call sites are completed explicitly instead.
            if (cutAt == 0)
            {
                stats.SkippedUnsupported++;
                continue;
            }

            if (catalog != null && containingSymbol != null
                && HasConflictingSignature(catalog, model, containingSymbol, ".ctor", parameters, cutAt))
            {
                stats.SkippedConflict++;
                continue;
            }

            var overload = BuildCtorOverload(ctor, parameters, cutAt, isStruct, model, paramsContext);
            if (overload != null)
            {
                result.Add(overload);
                stats.OverloadsGenerated++;
            }
        }

        return result;
    }

    public static List<LocalFunctionStatementSyntax> BuildOverloads(
        LocalFunctionStatementSyntax localFunc, SemanticModel model,
        DefaultParameterStripperStatistics stats,
        ParamsOverloadContext? paramsContext = null)
    {
        var result = new List<LocalFunctionStatementSyntax>();
        var parameters = localFunc.ParameterList.Parameters;
        var cutPoints = CollectCutPoints(parameters);
        if (cutPoints.Count == 0) return result;

        var isVoid = IsVoidType(localFunc.ReturnType);
        foreach (var cutAt in cutPoints)
        {
            var overload = BuildLocalFuncOverload(localFunc, parameters, cutAt, isVoid, model, paramsContext);
            if (overload != null)
            {
                result.Add(overload);
                stats.OverloadsGenerated++;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds forwarding constructors for a record / class / struct primary
    /// constructor whose default values were stripped.
    /// </summary>
    public static List<ConstructorDeclarationSyntax> BuildPrimaryConstructorOverloads(
        TypeDeclarationSyntax type, SemanticModel model,
        DefaultParameterStripperStatistics stats, bool isStruct,
        ParamsOverloadContext? paramsContext = null)
    {
        var result = new List<ConstructorDeclarationSyntax>();
        var parameterList = type.ParameterList;
        if (parameterList == null) return result;

        var parameters = parameterList.Parameters;
        var cutPoints = CollectCutPoints(parameters);
        if (cutPoints.Count == 0) return result;

        var identifier = SyntaxFactory.Identifier(type.Identifier.Text);
        foreach (var cutAt in cutPoints)
        {
            // See BuildOverloads(ConstructorDeclarationSyntax): never add a
            // parameterless constructor.
            if (cutAt == 0)
            {
                stats.SkippedUnsupported++;
                continue;
            }

            var retained = BuildRetainedParams(parameters, cutAt);
            var args = BuildForwardingArgs(parameters, cutAt, model, paramsContext);
            var initializer = SyntaxFactory.ConstructorInitializer(
                SyntaxKind.ThisConstructorInitializer,
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

            var overload = SyntaxFactory.ConstructorDeclaration(identifier)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(retained)))
                .WithInitializer(initializer)
                .WithBody(SyntaxFactory.Block());

            result.Add(overload);
            stats.OverloadsGenerated++;
        }

        return result;
    }

    // --- core helpers -----------------------------------------------------

    private static bool HasConflictingSignature(
        DefaultParameterCatalog catalog,
        SemanticModel model,
        ISymbol containingSymbol,
        string memberName,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        int cutAt)
    {
        var semanticTypes = DefaultParameterCatalog.BuildParamTypeString(model, parameters, cutAt);
        if (catalog.HasExistingSignature(containingSymbol, memberName, semanticTypes)
            || catalog.HasPendingOverload(containingSymbol, memberName, semanticTypes))
        {
            return true;
        }

        // Syntax-level duplicate detection, which keeps working when the semantic
        // model cannot resolve the parameter types (missing package references).
        var syntaxTypes = DefaultParameterCatalog.BuildParamTypeStringSyntax(parameters, cutAt);
        if (catalog.HasExistingSignature(containingSymbol, memberName, syntaxTypes)
            || catalog.HasPendingOverload(containingSymbol, memberName, syntaxTypes))
        {
            return true;
        }

        // Reject an overload that would be ambiguous with an ORIGINAL member of
        // the same arity but different parameter types. Only declarations seen in
        // pass 1 count: two generated overloads may legitimately share an arity
        // (Utf8JsonWriter(Stream) / Utf8JsonWriter(IBufferWriter<byte>)) and
        // rejecting them would break every call site.
        if (catalog.WouldCreateAmbiguityForType(containingSymbol, memberName, cutAt, semanticTypes)
            || catalog.WouldCreateAmbiguityForType(containingSymbol, memberName, cutAt, syntaxTypes))
        {
            return true;
        }

        catalog.RegisterPendingOverload(containingSymbol, memberName, semanticTypes);
        catalog.RegisterPendingOverload(containingSymbol, memberName, syntaxTypes);
        return false;
    }

    private static ConstructorDeclarationSyntax? BuildCtorOverload(
        ConstructorDeclarationSyntax original,
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt,
        bool isStruct,
        SemanticModel? model = null,
        ParamsOverloadContext? paramsContext = null)
    {
        var retained = BuildRetainedParams(parameters, cutAt);
        var args = BuildForwardingArgs(parameters, cutAt, model, paramsContext);
        var initializer = SyntaxFactory.ConstructorInitializer(
            SyntaxKind.ThisConstructorInitializer,
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

        // A struct parameterless constructor must be public (CS8958).
        var modifiers = retained.Count == 0 && isStruct
            ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            : FilterModifiers(original.Modifiers);

        return SyntaxFactory.ConstructorDeclaration(original.Identifier)
            .WithModifiers(modifiers)
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(retained)))
            .WithInitializer(initializer)
            .WithBody(SyntaxFactory.Block())
            .WithoutDeclarationLeadingTrivia();
    }

    private static LocalFunctionStatementSyntax? BuildLocalFuncOverload(
        LocalFunctionStatementSyntax original,
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt, bool isVoid,
        SemanticModel? model = null,
        ParamsOverloadContext? paramsContext = null)
    {
        var retained = BuildRetainedParams(parameters, cutAt);
        var args = BuildForwardingArgs(parameters, cutAt, model, paramsContext);
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(original.Identifier),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

        return SyntaxFactory.LocalFunctionStatement(original.ReturnType, original.Identifier)
            .WithModifiers(original.Modifiers)
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(retained)))
            .WithTypeParameterList(original.TypeParameterList)
            .WithConstraintClauses(original.ConstraintClauses)
            .WithBody(SyntaxFactory.Block(BuildForwardingStatement(invocation, isVoid)))
            .WithoutDeclarationLeadingTrivia();
    }

    /// <summary>
    /// The first token of a declaration carries the declaration's leading trivia,
    /// which includes directive trivia such as <c>#region</c> (the conditional
    /// pass removes every <c>#if</c> but leaves regions in place). Reusing
    /// <c>method.ReturnType</c> / <c>method.Modifiers</c> / the identifier would
    /// copy that block onto every generated overload, leaving an unmatched
    /// <c>#region</c> and CS1038 in the preprocessed project.
    /// </summary>
    private static T WithoutDeclarationLeadingTrivia<T>(this T node) where T : SyntaxNode
        => (T)node.WithLeadingTrivia();

    private static StatementSyntax BuildForwardingStatement(
        ExpressionSyntax invocation, bool isVoid)
        => isVoid
            ? SyntaxFactory.ExpressionStatement(invocation)
            : SyntaxFactory.ReturnStatement(invocation);

    private static StatementSyntax BuildForwardingStatement(
        ExpressionSyntax target, List<ArgumentSyntax> args, bool isVoid)
        => BuildForwardingStatement(
            SyntaxFactory.InvocationExpression(target,
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args))),
            isVoid);

    /// <summary>
    /// Builds the invocation target, adding explicit type arguments for generic
    /// methods so the forwarding call binds to the same method.
    /// </summary>
    private static ExpressionSyntax BuildInvocationTarget(MethodDeclarationSyntax method)
    {
        if (method.TypeParameterList is { Parameters.Count: > 0 } typeParameters)
        {
            var typeArguments = SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SeparatedList(
                    typeParameters.Parameters.Select(p => (TypeSyntax)SyntaxFactory.IdentifierName(p.Identifier))));
            return SyntaxFactory.GenericName(method.Identifier, typeArguments);
        }

        return SyntaxFactory.IdentifierName(method.Identifier);
    }

    /// <summary>
    /// Collects every parameter index that owns a default value and can safely
    /// start an overload: all parameters from that index on must be optional
    /// (have a default or be <c>params</c>), otherwise C# requires the caller to
    /// supply them and no shorter overload can exist.
    /// </summary>
    private static List<int> CollectCutPoints(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        var cutPoints = new List<int>();
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Default == null || IsParams(parameters[i]))
            {
                continue;
            }

            var tailIsOptional = true;
            for (var j = i; j < parameters.Count; j++)
            {
                if (parameters[j].Default == null && !IsParams(parameters[j]))
                {
                    tailIsOptional = false;
                    break;
                }
            }

            if (tailIsOptional)
            {
                cutPoints.Add(i);
            }
        }
        return cutPoints;
    }

    private static List<ParameterSyntax> BuildRetainedParams(
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt)
    {
        var retained = new List<ParameterSyntax>(cutAt);
        for (var i = 0; i < cutAt && i < parameters.Count; i++)
        {
            retained.Add(parameters[i].WithDefault(null));
        }
        return retained;
    }

    private static List<ArgumentSyntax> BuildForwardingArgs(
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt,
        SemanticModel? model = null,
        ParamsOverloadContext? paramsContext = null)
    {
        var args = new List<ArgumentSyntax>(parameters.Count);
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i < cutAt)
            {
                var parameter = parameters[i];
                var expression = SyntaxFactory.IdentifierName(parameter.Identifier);
                var modifier = parameter.Modifiers.FirstOrDefault(m =>
                    m.IsKind(SyntaxKind.RefKeyword)
                    || m.IsKind(SyntaxKind.OutKeyword)
                    || m.IsKind(SyntaxKind.InKeyword));

                args.Add(modifier.IsKind(SyntaxKind.None)
                    ? SyntaxFactory.Argument(expression)
                    : SyntaxFactory.Argument(default, modifier, expression));
            }
            else if (parameters[i].Default != null)
            {
                args.Add(SyntaxFactory.Argument(parameters[i].Default!.Value));
            }
            else if (IsParams(parameters[i]) && parameters[i].Type is ArrayTypeSyntax arrayType)
            {
                // params parameter with no declared default: forward an empty array.
                args.Add(SyntaxFactory.Argument(
                    SyntaxFactory.ArrayCreationExpression(arrayType.WithRankSpecifiers(
                            SyntaxFactory.SingletonList(SyntaxFactory.ArrayRankSpecifier(
                                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                                        SyntaxFactory.Literal(0)))))),
                        null)));
            }
            else if (IsParams(parameters[i]) && paramsContext != null && model != null)
            {
                // C48 (PC-7): non-array params slot — forward the
                // kind-appropriate empty-collection shape; builder/unsupported
                // kinds omit the argument and emit their skip diagnostic.
                var expression = ParamsOverloadForwarding.BuildEmptySlotExpression(
                    parameters[i], model, paramsContext);
                if (expression != null)
                {
                    args.Add(SyntaxFactory.Argument(expression));
                }
            }
        }
        return args;
    }

    private static bool IsVoidType(TypeSyntax type)
        => type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword };

    private static SyntaxTokenList FilterModifiers(SyntaxTokenList original)
    {
        var kept = new List<SyntaxToken>();
        foreach (var modifier in original)
        {
            if (modifier.IsKind(SyntaxKind.AbstractKeyword)
                || modifier.IsKind(SyntaxKind.ExternKeyword)
                || modifier.IsKind(SyntaxKind.PartialKeyword)
                || modifier.IsKind(SyntaxKind.VirtualKeyword)
                || modifier.IsKind(SyntaxKind.OverrideKeyword)
                || modifier.IsKind(SyntaxKind.AsyncKeyword)
                || modifier.IsKind(SyntaxKind.SealedKeyword))
            {
                continue;
            }
            kept.Add(modifier);
        }
        return SyntaxFactory.TokenList(kept);
    }

    private static bool IsParams(ParameterSyntax parameter)
        => parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));
}
