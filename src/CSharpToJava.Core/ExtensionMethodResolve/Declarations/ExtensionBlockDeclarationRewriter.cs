using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Declaration materialization (EMD-METHOD/EMD-PROP): replaces every
/// annotated extension block node, a direct member of its host class, with
/// classic static members. Block methods become
/// <c>static R M&lt;TBlock...,TMember...&gt;(this Receiver recv, A a)</c>
/// (receiver keeps its original name, so bodies are unchanged); block
/// properties become strictly-named <c>get_X</c>/<c>set_X</c> methods,
/// generated only for the accessors that exist. Naming/modifier/attribute
/// rules reuse <see cref="AccessorsToMethods"/> and
/// <see cref="AttributeRouting"/>; no backing field is ever generated.
/// </summary>
internal sealed class ExtensionBlockDeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<SyntaxAnnotation, BlockPlan> _plansByAnnotation;

    public ExtensionBlockDeclarationRewriter(SemanticModel model, IEnumerable<BlockPlan> plans)
    {
        _model = model;
        _plansByAnnotation = plans.ToDictionary(p => p.BlockAnnotation);
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax?)base.VisitClassDeclaration(node) ?? node;
        if (!visited.Members.Any(HasBlockAnnotation))
        {
            return visited;
        }

        var expanded = new List<MemberDeclarationSyntax>();
        foreach (var member in visited.Members)
        {
            var plan = FindPlan(member);
            if (plan is null)
            {
                expanded.Add(member);
                continue;
            }

            var generated = Materialize(plan);
            if (generated.Count > 0)
            {
                generated[0] = generated[0].WithLeadingTrivia(member.GetLeadingTrivia());
                generated[^1] = generated[^1].WithTrailingTrivia(member.GetTrailingTrivia());
            }

            expanded.AddRange(generated);
        }

        return visited.WithMembers(SyntaxFactory.List(expanded));
    }

    private List<MemberDeclarationSyntax> Materialize(BlockPlan plan)
    {
        var generated = new List<MemberDeclarationSyntax>();
        foreach (var member in plan.Members)
        {
            switch (member)
            {
                case MethodMemberPlan method:
                    generated.Add(BuildMethod(plan, method));
                    break;
                case PropertyMemberPlan property:
                    generated.AddRange(BuildProperty(plan, property));
                    break;
            }
        }

        // Built from trivia-stripped pieces; normalize each member so token
        // streams are syntactically valid and readable in the host.
        return generated.Select(member => member.NormalizeWhitespace()).ToList();
    }

    private static MethodDeclarationSyntax BuildMethod(BlockPlan plan, MethodMemberPlan member)
    {
        var method = (MethodDeclarationSyntax)member.Declaration;
        var modifiers = BuildMethodModifiers(method.Modifiers);
        var typeParameters = GenericParameterMapping.MergeTypeParameters(
            plan.BlockTypeParameters, member.MemberTypeParameters);
        var constraints = GenericParameterMapping.MergeConstraints(
            plan.BlockConstraints, member.MemberConstraints);
        var receiverParameter = GenericParameterMapping.BuildReceiverParameter(plan.Receiver);
        var parameters = method.ParameterList.Parameters.Insert(0, receiverParameter);

        return SyntaxFactory.MethodDeclaration(
            method.AttributeLists,
            modifiers,
            method.ReturnType,
            explicitInterfaceSpecifier: null,
            method.Identifier,
            typeParameters,
            SyntaxFactory.ParameterList(parameters),
            constraints,
            method.Body,
            method.ExpressionBody)
            .WithSemicolonToken(method.SemicolonToken);
    }

    private static SyntaxTokenList BuildMethodModifiers(SyntaxTokenList source)
    {
        var accessibility = new List<SyntaxToken>();
        var rest = new List<SyntaxToken>();

        foreach (var token in source)
        {
            if (AccessorsToMethods.IsAccessibility(token))
            {
                accessibility.Add(token);
                continue;
            }

            switch (token.Kind())
            {
                // Block members cannot legally carry these; drop defensively.
                case SyntaxKind.VirtualKeyword:
                case SyntaxKind.AbstractKeyword:
                case SyntaxKind.OverrideKeyword:
                case SyntaxKind.SealedKeyword:
                    continue;
            }

            rest.Add(token);
        }

        var tokens = new List<SyntaxToken>(accessibility)
        {
            SyntaxFactory.Token(SyntaxKind.StaticKeyword),
        };
        tokens.AddRange(rest);
        return SyntaxFactory.TokenList(tokens);
    }

    private IEnumerable<MethodDeclarationSyntax> BuildProperty(
        BlockPlan plan,
        PropertyMemberPlan member)
    {
        var decl = member.PropertyDeclaration;
        var getterName = AccessorsToMethods.GetterName(member.Name);
        var setterName = AccessorsToMethods.SetterName(member.Name);

        // Expression-bodied property (no accessor list): get-only.
        if (decl.AccessorList is null && decl.ExpressionBody is { } expressionBody)
        {
            var modifiers = AddStatic(
                AccessorsToMethods.BuildMethodModifiers(decl, null!, inInterface: false, isExplicitInterfaceImplementation: false));
            var attributes = RouteAttributes(decl, accessor: null, forGetter: true);
            yield return SyntaxFactory.MethodDeclaration(
                attributes,
                modifiers,
                decl.Type,
                null,
                SyntaxFactory.Identifier(getterName),
                null,
                SyntaxFactory.ParameterList(
                    SyntaxFactory.SingletonSeparatedList(
                        GenericParameterMapping.BuildReceiverParameter(plan.Receiver))),
                SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                null,
                expressionBody)
                .WithSemicolonToken(decl.SemicolonToken);
            yield break;
        }

        if (decl.AccessorList is null)
        {
            yield break;
        }

        foreach (var acc in decl.AccessorList.Accessors)
        {
            if (acc.IsKind(SyntaxKind.GetAccessorDeclaration) && member.HasGet)
            {
                var modifiers = AddStatic(
                    AccessorsToMethods.BuildMethodModifiers(decl, acc, false, false));
                var attributes = RouteAttributes(decl, acc, forGetter: true);
                yield return SyntaxFactory.MethodDeclaration(
                    attributes,
                    modifiers,
                    decl.Type,
                    null,
                    SyntaxFactory.Identifier(getterName),
                    null,
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            GenericParameterMapping.BuildReceiverParameter(plan.Receiver))),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    acc.Body,
                    acc.ExpressionBody)
                    .WithSemicolonToken(acc.SemicolonToken);
            }
            else if (acc.IsKind(SyntaxKind.SetAccessorDeclaration) && member.HasSet)
            {
                var modifiers = AddStatic(
                    AccessorsToMethods.BuildMethodModifiers(decl, acc, false, false));
                var attributes = RouteAttributes(decl, acc, forGetter: false);
                var valueParameter = SyntaxFactory.Parameter(
                    default,
                    SyntaxFactory.TokenList(),
                    decl.Type.WithoutTrivia(),
                    SyntaxFactory.Identifier("value"),
                    null);
                var parameters = SyntaxFactory.SeparatedList(new[]
                {
                    GenericParameterMapping.BuildReceiverParameter(plan.Receiver),
                    valueParameter,
                });
                yield return SyntaxFactory.MethodDeclaration(
                    attributes,
                    modifiers,
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    null,
                    SyntaxFactory.Identifier(setterName),
                    null,
                    SyntaxFactory.ParameterList(parameters),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    acc.Body,
                    acc.ExpressionBody)
                    .WithSemicolonToken(acc.SemicolonToken);
            }
        }
    }

    private SyntaxList<AttributeListSyntax> RouteAttributes(
        PropertyDeclarationSyntax decl,
        AccessorDeclarationSyntax? accessor,
        bool forGetter)
    {
        var lists = new List<AttributeListSyntax>(accessor?.AttributeLists ?? new SyntaxList<AttributeListSyntax>());
        foreach (var list in decl.AttributeLists)
        {
            var destination = AttributeRouting.RouteList(list, _model, fieldGenerated: false);
            if (forGetter)
            {
                if (destination is AttributeRouting.Destination.Getter
                    or AttributeRouting.Destination.Methods)
                {
                    lists.Add(list.WithTarget(null));
                }
            }
            else if (destination == AttributeRouting.Destination.Methods)
            {
                lists.Add(list.WithTarget(null));
            }
        }

        return SyntaxFactory.List(lists);
    }

    private static SyntaxTokenList AddStatic(SyntaxTokenList modifiers)
        => SyntaxFactory.TokenList(
            modifiers.Append(SyntaxFactory.Token(SyntaxKind.StaticKeyword)));

    private bool HasBlockAnnotation(MemberDeclarationSyntax member)
        => member.GetAnnotations("C49").Any(a => a.Data == "BlockDeclaration");

    private BlockPlan? FindPlan(MemberDeclarationSyntax member)
    {
        foreach (var annotation in member.GetAnnotations("C49"))
        {
            if (_plansByAnnotation.TryGetValue(annotation, out var plan))
            {
                return plan;
            }
        }

        return null;
    }
}
