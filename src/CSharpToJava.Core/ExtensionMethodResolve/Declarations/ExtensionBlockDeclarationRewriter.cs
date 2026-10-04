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
            if (plan is null or { HasDeferredSites: true })
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
                case OperatorMemberPlan op:
                    generated.Add(BuildOperator(plan, op));
                    break;
                case StaticMethodMemberPlan staticMethod:
                    generated.Add(BuildStaticMethod(plan, staticMethod));
                    break;
                case StaticPropertyMemberPlan staticProperty:
                    generated.AddRange(BuildStaticProperty(plan, staticProperty));
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

    // ---- M2 builders: operators, static methods, static properties ----

    private static MethodDeclarationSyntax BuildOperator(BlockPlan plan, OperatorMemberPlan member)
    {
        var decl = member.OperatorDeclaration;
        var modifiers = BuildStaticMemberModifiers(decl.Modifiers);
        var typeParameters = GenericParameterMapping.MergeTypeParameters(
            plan.BlockTypeParameters, Array.Empty<TypeParameterSyntax>());
        var constraints = GenericParameterMapping.MergeConstraints(
            plan.BlockConstraints, Array.Empty<TypeParameterConstraintClauseSyntax>());

        return SyntaxFactory.MethodDeclaration(
            decl.AttributeLists,
            modifiers,
            decl.ReturnType,
            explicitInterfaceSpecifier: null,
            SyntaxFactory.Identifier(member.GeneratedName),
            typeParameters,
            decl.ParameterList,
            constraints,
            decl.Body,
            decl.ExpressionBody)
            .WithSemicolonToken(decl.SemicolonToken);
    }

    private static MethodDeclarationSyntax BuildStaticMethod(
        BlockPlan plan, StaticMethodMemberPlan member)
    {
        var method = (MethodDeclarationSyntax)member.Declaration;
        var modifiers = BuildStaticMemberModifiers(method.Modifiers);
        var typeParameters = GenericParameterMapping.MergeTypeParameters(
            plan.BlockTypeParameters, member.MemberTypeParameters);
        var constraints = GenericParameterMapping.MergeConstraints(
            plan.BlockConstraints, member.MemberConstraints);

        return SyntaxFactory.MethodDeclaration(
            method.AttributeLists,
            modifiers,
            method.ReturnType,
            explicitInterfaceSpecifier: null,
            method.Identifier,
            typeParameters,
            method.ParameterList,
            constraints,
            method.Body,
            method.ExpressionBody)
            .WithSemicolonToken(method.SemicolonToken);
    }

    private IEnumerable<MethodDeclarationSyntax> BuildStaticProperty(
        BlockPlan plan,
        StaticPropertyMemberPlan member)
    {
        var decl = member.PropertyDeclaration;
        var getterName = AccessorsToMethods.GetterName(member.Name);
        var setterName = AccessorsToMethods.SetterName(member.Name);

        TypeParameterListSyntax? blockTypeParameters = plan.BlockTypeParameters.Count > 0
            ? GenericParameterMapping.MergeTypeParameters(
                plan.BlockTypeParameters, Array.Empty<TypeParameterSyntax>())
            : null;
        var blockConstraints = SyntaxFactory.List(plan.BlockConstraints);

        // Expression-bodied property (no accessor list): get-only.
        if (decl.AccessorList is null && decl.ExpressionBody is { } expressionBody)
        {
            var modifiers = EnsureStatic(
                AccessorsToMethods.BuildMethodModifiers(decl, null!, inInterface: false, isExplicitInterfaceImplementation: false));
            var attributes = RouteAttributes(decl, accessor: null, forGetter: true);
            yield return SyntaxFactory.MethodDeclaration(
                attributes,
                modifiers,
                decl.Type,
                null,
                SyntaxFactory.Identifier(getterName),
                blockTypeParameters,
                SyntaxFactory.ParameterList(),
                blockConstraints,
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
                var modifiers = EnsureStatic(
                    AccessorsToMethods.BuildMethodModifiers(decl, acc, false, false));
                var attributes = RouteAttributes(decl, acc, forGetter: true);
                yield return SyntaxFactory.MethodDeclaration(
                    attributes,
                    modifiers,
                    decl.Type,
                    null,
                    SyntaxFactory.Identifier(getterName),
                    blockTypeParameters,
                    SyntaxFactory.ParameterList(),
                    blockConstraints,
                    acc.Body,
                    acc.ExpressionBody)
                    .WithSemicolonToken(acc.SemicolonToken);
            }
            else if (acc.IsKind(SyntaxKind.SetAccessorDeclaration) && member.HasSet)
            {
                var modifiers = EnsureStatic(
                    AccessorsToMethods.BuildMethodModifiers(decl, acc, false, false));
                var attributes = RouteAttributes(decl, acc, forGetter: false);
                var valueParameter = SyntaxFactory.Parameter(
                    default,
                    SyntaxFactory.TokenList(),
                    decl.Type.WithoutTrivia(),
                    SyntaxFactory.Identifier("value"),
                    null);
                yield return SyntaxFactory.MethodDeclaration(
                    attributes,
                    modifiers,
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    null,
                    SyntaxFactory.Identifier(setterName),
                    blockTypeParameters,
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(valueParameter)),
                    blockConstraints,
                    acc.Body,
                    acc.ExpressionBody)
                    .WithSemicolonToken(acc.SemicolonToken);
            }
        }
    }

    /// <summary>
    /// Modifiers for a member whose source declaration is already static
    /// (operators/static methods/static properties): keep accessibility,
    /// guarantee exactly one static token (after accessibility), drop
    /// inheritance modifiers that cannot legally appear.
    /// </summary>
    private static SyntaxTokenList BuildStaticMemberModifiers(SyntaxTokenList source)
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

            // Consume the source static token defensively; exactly one is
            // re-emitted after accessibility below (duplicates must not
            // survive even though block syntax always requires one).
            if (token.IsKind(SyntaxKind.StaticKeyword))
            {
                continue;
            }

            switch (token.Kind())
            {
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

    /// <summary>Ensure a modifier list carries exactly one static token.</summary>
    private static SyntaxTokenList EnsureStatic(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.StaticKeyword))
        {
            return modifiers;
        }

        return SyntaxFactory.TokenList(
            modifiers.Append(SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
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
