using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// Constructs overload declarations from a stripped original.
/// For each cut point (a parameter with a default value, excluding params),
/// generates a method/constructor with fewer parameters that forwards to
/// the original using the verbatim default value expressions.
/// </summary>
internal static class DefaultParameterOverloadBuilder
{
    public static List<MethodDeclarationSyntax> BuildOverloads(
        MethodDeclarationSyntax method, SemanticModel model,
        DefaultParameterStripperStatistics stats)
        => BuildOverloadsCore(method, method.ParameterList.Parameters,
            method.Modifiers, method.ReturnType, method.Identifier,
            method.TypeParameterList, method.ConstraintClauses,
            stats, isMethod: true);

    public static List<ConstructorDeclarationSyntax> BuildOverloads(
        ConstructorDeclarationSyntax ctor, SemanticModel model,
        DefaultParameterStripperStatistics stats)
    {
        var result = new List<ConstructorDeclarationSyntax>();
        var indices = CollectDefaultIndices(ctor.ParameterList.Parameters);
        if (indices.Count == 0) return result;
        if (!ValidateOptionalTail(ctor.ParameterList.Parameters, indices[0]))
        {
            stats.SkippedUnsupported++;
            return result;
        }
        foreach (var cutAt in indices.Where(i => i >= indices[0]))
        {
            var overload = BuildCtorOverload(ctor, ctor.ParameterList.Parameters, cutAt);
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
        DefaultParameterStripperStatistics stats)
    {
        var result = new List<LocalFunctionStatementSyntax>();
        var indices = CollectDefaultIndices(localFunc.ParameterList.Parameters);
        if (indices.Count == 0) return result;
        if (!ValidateOptionalTail(localFunc.ParameterList.Parameters, indices[0]))
        {
            stats.SkippedUnsupported++;
            return result;
        }
        foreach (var cutAt in indices.Where(i => i >= indices[0]))
        {
            var overload = BuildLocalFuncOverload(localFunc, localFunc.ParameterList.Parameters, cutAt);
            if (overload != null)
            {
                result.Add(overload);
                stats.OverloadsGenerated++;
            }
        }
        return result;
    }

    // --- core helpers ---

    private static List<MethodDeclarationSyntax> BuildOverloadsCore(
        MethodDeclarationSyntax method,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        SyntaxTokenList modifiers,
        TypeSyntax returnType,
        SyntaxToken identifier,
        TypeParameterListSyntax? typeParamList,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        DefaultParameterStripperStatistics stats,
        bool isMethod)
    {
        var result = new List<MethodDeclarationSyntax>();
        var indices = CollectDefaultIndices(parameters);
        if (indices.Count == 0) return result;
        if (!ValidateOptionalTail(parameters, indices[0]))
        {
            stats.SkippedUnsupported++;
            return result;
        }

        var isVoid = returnType is PredefinedTypeSyntax pt
            && pt.Keyword.IsKind(SyntaxKind.VoidKeyword);
        var filteredModifiers = FilterModifiers(modifiers);

        foreach (var cutAt in indices.Where(i => i >= indices[0]))
        {
            var retainedParams = new List<ParameterSyntax>();
            for (var i = 0; i < cutAt; i++)
                retainedParams.Add(parameters[i].WithDefault(null));

            var args = BuildForwardingArgs(parameters, cutAt);

            // Build invocation with explicit type args for generic methods
            ExpressionSyntax target;
            if (typeParamList != null && typeParamList.Parameters.Count > 0)
            {
                var typeArgs = string.Join(", ", typeParamList.Parameters.Select(p => p.Identifier.Text));
                target = SyntaxFactory.IdentifierName(
                    SyntaxFactory.Identifier(identifier.Text + "<" + typeArgs + ">"));
            }
            else
            {
                target = SyntaxFactory.IdentifierName(identifier);
            }

            var invocation = SyntaxFactory.InvocationExpression(target,
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

            StatementSyntax bodyStmt = isVoid
                ? SyntaxFactory.ExpressionStatement(invocation)
                : SyntaxFactory.ReturnStatement(invocation);

            var overload = SyntaxFactory.MethodDeclaration(returnType, identifier)
                .WithModifiers(filteredModifiers)
                .WithParameterList(SyntaxFactory.ParameterList(
                    SyntaxFactory.SeparatedList(retainedParams)))
                .WithTypeParameterList(typeParamList)
                .WithConstraintClauses(constraintClauses)
                .WithBody(SyntaxFactory.Block(bodyStmt));

            result.Add(overload);
            stats.OverloadsGenerated++;
        }
        return result;
    }

    private static ConstructorDeclarationSyntax? BuildCtorOverload(
        ConstructorDeclarationSyntax original,
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt)
    {
        var retainedParams = new List<ParameterSyntax>();
        for (var i = 0; i < cutAt; i++)
            retainedParams.Add(parameters[i].WithDefault(null));

        var args = BuildForwardingArgs(parameters, cutAt);
        var initializer = SyntaxFactory.ConstructorInitializer(
            SyntaxKind.ThisConstructorInitializer,
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

        return SyntaxFactory.ConstructorDeclaration(original.Identifier)
            .WithModifiers(FilterModifiers(original.Modifiers))
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList(retainedParams)))
            .WithInitializer(initializer)
            .WithBody(SyntaxFactory.Block());
    }

    private static LocalFunctionStatementSyntax? BuildLocalFuncOverload(
        LocalFunctionStatementSyntax original,
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt)
    {
        var retainedParams = new List<ParameterSyntax>();
        for (var i = 0; i < cutAt; i++)
            retainedParams.Add(parameters[i].WithDefault(null));

        var args = BuildForwardingArgs(parameters, cutAt);
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(original.Identifier),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

        var isVoid = original.ReturnType is PredefinedTypeSyntax pt
            && pt.Keyword.IsKind(SyntaxKind.VoidKeyword);
        StatementSyntax bodyStmt = isVoid
            ? SyntaxFactory.ExpressionStatement(invocation)
            : SyntaxFactory.ReturnStatement(invocation);

        return SyntaxFactory.LocalFunctionStatement(original.ReturnType, original.Identifier)
            .WithModifiers(original.Modifiers)
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList(retainedParams)))
            .WithTypeParameterList(original.TypeParameterList)
            .WithBody(SyntaxFactory.Block(bodyStmt));
    }

    private static List<int> CollectDefaultIndices(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        var indices = new List<int>();
        for (var i = 0; i < parameters.Count; i++)
            if (parameters[i].Default != null && !IsParams(parameters[i]))
                indices.Add(i);
        return indices;
    }

    private static bool ValidateOptionalTail(
        SeparatedSyntaxList<ParameterSyntax> parameters, int firstDefaultIdx)
    {
        for (var i = firstDefaultIdx; i < parameters.Count; i++)
            if (parameters[i].Default == null && !IsParams(parameters[i]))
                return false; // CS1737
        return true;
    }

    private static List<ArgumentSyntax> BuildForwardingArgs(
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt)
    {
        var args = new List<ArgumentSyntax>();
        for (var i = 0; i < parameters.Count; i++)
        {
            args.Add(i < cutAt
                ? SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameters[i].Identifier))
                : SyntaxFactory.Argument(parameters[i].Default!.Value));
        }
        return args;
    }

    private static SyntaxTokenList FilterModifiers(SyntaxTokenList original)
    {
        var kept = new List<SyntaxToken>();
        foreach (var mod in original)
        {
            if (mod.IsKind(SyntaxKind.AbstractKeyword) || mod.IsKind(SyntaxKind.ExternKeyword)
                || mod.IsKind(SyntaxKind.PartialKeyword) || mod.IsKind(SyntaxKind.VirtualKeyword)
                || mod.IsKind(SyntaxKind.OverrideKeyword) || mod.IsKind(SyntaxKind.AsyncKeyword)
                || mod.IsKind(SyntaxKind.SealedKeyword))
                continue;
            kept.Add(mod);
        }
        return SyntaxFactory.TokenList(kept);
    }

    private static bool IsParams(ParameterSyntax param)
        => param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));
}
