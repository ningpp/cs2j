using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// CSharpSyntaxRewriter that strips default parameter values from
/// methods, constructors, and local functions, and generates equivalent
/// forwarding overload methods.
///
/// Strategy: When visiting a type declaration (class/struct/interface),
/// we iterate over its members, strip defaults, generate overloads,
/// and build a new member list with the overloads inserted after
/// each original member.
/// </summary>
internal sealed class DefaultParameterDeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;
    private readonly DefaultParameterStripperOptions _options;
    private readonly DefaultParameterStripperStatistics _stats;
    private readonly List<DefaultParameterStripDiagnostic> _diagnostics;
    private readonly DefaultParameterCatalog _catalog;

    public bool Changed { get; private set; }

    public DefaultParameterDeclarationRewriter(
        SemanticModel? model,
        DefaultParameterStripperOptions options,
        DefaultParameterStripperStatistics stats,
        List<DefaultParameterStripDiagnostic> diagnostics,
        DefaultParameterCatalog catalog)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _catalog = catalog;
    }

    // -----------------------------------------------------------------------
    // Type declarations: process members, insert overloads
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (!HasDirtyMembers(node.Members))
            return base.VisitClassDeclaration(node);
        return VisitTypeDeclarationCore(node, (n, members) => n.WithMembers(members));
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        if (!HasDirtyMembers(node.Members))
            return base.VisitStructDeclaration(node);
        return VisitTypeDeclarationCore(node, (n, members) => n.WithMembers(members));
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        if (!HasDirtyMembers(node.Members))
            return base.VisitInterfaceDeclaration(node);
        return VisitTypeDeclarationCore(node, (n, members) => n.WithMembers(members));
    }

    private SyntaxNode? VisitTypeDeclarationCore<T>(
        T node,
        Func<T, SyntaxList<MemberDeclarationSyntax>, T> withMembers)
        where T : TypeDeclarationSyntax
    {

        var newMembers = new List<MemberDeclarationSyntax>();
        var isInterface = node is InterfaceDeclarationSyntax;

        foreach (var member in node.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    ProcessMethod(method, newMembers, isInterface);
                    break;
                case ConstructorDeclarationSyntax ctor:
                    ProcessConstructor(ctor, newMembers);
                    break;
                default:
                    // Recursively visit (handles nested types)
                    var visited = (MemberDeclarationSyntax?)Visit(member);
                    if (visited != null)
                        newMembers.Add(visited);
                    break;
            }
        }

        // Also recursively visit members we didn't specifically process
        // (e.g. nested types) — but we already handled that in the default case above.
        var result = withMembers(node, SyntaxFactory.List(newMembers));
        // Note: Changed is set by ProcessMethod/ProcessConstructor when they strip defaults
        return result;
    }

    // -----------------------------------------------------------------------
    // Method processing
    // -----------------------------------------------------------------------

    private void ProcessMethod(
        MethodDeclarationSyntax method,
        List<MemberDeclarationSyntax> output,
        bool isInterface)
    {
        if (!HasDefaults(method.ParameterList.Parameters))
        {
            output.Add(method);
            return;
        }

        // Skip partial / extern (spec §4.1)
        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword) || m.IsKind(SyntaxKind.ExternKeyword)))
        {
            _stats.SkippedUnsupported++;
            output.Add(method);
            return;
        }

        // Explicit interface impl: strip defaults only, no overload (spec §4.1)
        // Override: strip defaults only, no overload (spec §4.1)
        bool stripOnly = method.ExplicitInterfaceSpecifier != null
            || method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));

        Changed = true;
        _stats.DeclarationsStripped++;
        CountStrippedParameters(method.ParameterList.Parameters);

        var strippedParams = StripDefaults(method.ParameterList);
        var strippedMethod = method.WithParameterList(strippedParams);
        output.Add(strippedMethod);

        if (stripOnly || !_options.GenerateOverloads)
            return;

        // Generate overloads
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, _model!, _stats);

        // For interface: convert overload bodies to expression-bodied
        if (isInterface)
            overloads = overloads.ConvertAll(ToExpressionBodied);

        foreach (var overload in overloads)
            output.Add(overload);
    }

    // -----------------------------------------------------------------------
    // Constructor processing
    // -----------------------------------------------------------------------

    private void ProcessConstructor(
        ConstructorDeclarationSyntax ctor,
        List<MemberDeclarationSyntax> output)
    {
        // Skip static constructors
        if (ctor.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
        {
            output.Add(ctor);
            return;
        }

        if (!HasDefaults(ctor.ParameterList.Parameters))
        {
            output.Add(ctor);
            return;
        }

        // Skip partial / extern
        if (ctor.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword) || m.IsKind(SyntaxKind.ExternKeyword)))
        {
            _stats.SkippedUnsupported++;
            output.Add(ctor);
            return;
        }

        Changed = true;
        _stats.DeclarationsStripped++;
        CountStrippedParameters(ctor.ParameterList.Parameters);

        var strippedParams = StripDefaults(ctor.ParameterList);
        var strippedCtor = ctor.WithParameterList(strippedParams);
        output.Add(strippedCtor);

        if (!_options.GenerateOverloads)
            return;

        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(ctor, _model!, _stats);
        foreach (var overload in overloads)
            output.Add(overload);
    }

    // -----------------------------------------------------------------------
    // Local function handling (inside method bodies)
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        if (node.ParameterList == null || !HasDefaults(node.ParameterList.Parameters))
            return base.VisitLocalFunctionStatement(node);

        // Skip partial / extern
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword) || m.IsKind(SyntaxKind.ExternKeyword)))
        {
            _stats.SkippedUnsupported++;
            return base.VisitLocalFunctionStatement(node);
        }

        Changed = true;
        _stats.DeclarationsStripped++;
        CountStrippedParameters(node.ParameterList.Parameters);

        var strippedParams = StripDefaults(node.ParameterList);
        var strippedNode = node.WithParameterList(strippedParams);

        // For local functions, overload generation would require inserting
        // sibling statements, which is complex. We just strip defaults.
        return base.VisitLocalFunctionStatement(strippedNode)!;
    }

    // -----------------------------------------------------------------------
    // Skip these declarations entirely (spec §4.1)
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        if (node.ParameterList != null && HasDefaults(node.ParameterList.Parameters))
        {
            _stats.SkippedUnsupported++;
        }
        return base.VisitDelegateDeclaration(node);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool HasDirtyMembers(SyntaxList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method when HasDefaults(method.ParameterList.Parameters):
                    return true;
                case ConstructorDeclarationSyntax ctor when HasDefaults(ctor.ParameterList.Parameters):
                    return true;
                case TypeDeclarationSyntax type when HasDirtyMembers(type.Members):
                    return true;
            }
        }
        return false;
    }

    private static ParameterListSyntax StripDefaults(ParameterListSyntax paramList)
    {
        var newParams = paramList.Parameters
            .Select(p => p.Default != null ? p.WithDefault(null) : p)
            .ToArray();
        return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParams));
    }

    private static bool HasDefaults(SeparatedSyntaxList<ParameterSyntax> parameters)
        => parameters.Any(p => p.Default != null);

    private void CountStrippedParameters(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        foreach (var p in parameters)
            if (p.Default != null)
                _stats.ParametersStripped++;
    }

    private static MethodDeclarationSyntax ToExpressionBodied(MethodDeclarationSyntax method)
    {
        // Convert { return X(...); } → => X(...)
        if (method.Body is { Statements.Count: 1 } body)
        {
            if (body.Statements[0] is ExpressionStatementSyntax es
                && es.Expression is InvocationExpressionSyntax inv)
            {
                return method.WithBody(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(inv))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            }
            if (body.Statements[0] is ReturnStatementSyntax rs
                && rs.Expression != null)
            {
                return method.WithBody(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                        rs.Expression!))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            }
        }
        return method;
    }
}
