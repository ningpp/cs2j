using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Family-wide single-evaluation temp-lifting machinery, generalized from the
/// C47 equality-concat TempLifter. Pass 1 (each rule's rewriter) collects
/// <see cref="PendingTemp"/> entries carrying a <b>statement sequence</b>
/// (declaration + Add/AddRange/foreach statements, not just one declaration);
/// <see cref="Apply"/> (Pass 2) inserts the sequences at their anchors.
/// All rules share the <c>__ed_</c> prefix and one per-file numbering space
/// (design hard invariant #4).
/// </summary>
internal static class ExpressionDesugarTempLifter
{
    /// <summary>Annotation kind used to keep anchor nodes locatable after rewrites.</summary>
    public const string AnchorAnnotationKind = "ed-anchor";

    internal sealed record PendingTemp(
        IReadOnlyList<StatementSyntax> Statements,
        SyntaxNode AnchorNode,
        int Order);

    /// <summary>
    /// Finds the nearest enclosing statement, which serves as the insertion
    /// anchor. When the site is in an expression-bodied member, the
    /// <see cref="ArrowExpressionClauseSyntax"/> is returned (the clause is
    /// converted to a block). Returns null in truly anchor-free contexts
    /// (field/property initializers, attribute arguments, default parameter
    /// values, query clauses, ...).
    /// </summary>
    internal static SyntaxNode? FindAnchor(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is StatementSyntax stmt)
                return stmt;
            if (current is ArrowExpressionClauseSyntax arrow)
                return arrow;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Inserts all collected statement sequences. When the anchor is a direct
    /// statement of a <see cref="BlockSyntax"/>, statements are inserted before
    /// it; when the anchor is inside a top-level (global) statement, they are
    /// inserted as preceding global statements; when the anchor is an embedded
    /// statement (if/while/...) or a switch-section statement, the anchor is
    /// wrapped in a block carrying the inserted statements, keeping scope and
    /// evaluation order intact.
    /// </summary>
    internal static CompilationUnitSyntax Apply(
        CompilationUnitSyntax root,
        List<PendingTemp> pending,
        IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> anchorAnnotations)
    {
        if (pending.Count == 0)
            return root;

        var byAnchor = pending
            .GroupBy(p => p.AnchorNode)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<StatementSyntax>)g
                    .OrderBy(p => p.Order)
                    .SelectMany(p => p.Statements)
                    .ToList());

        SyntaxNode current = root;
        foreach (var (anchor, statements) in byAnchor)
        {
            if (!anchorAnnotations.TryGetValue(anchor, out var annotation))
                continue;

            var currentAnchor = current.GetAnnotatedNodes(annotation)
                .FirstOrDefault();
            if (currentAnchor == null)
                continue;

            if (currentAnchor is ArrowExpressionClauseSyntax arrow)
            {
                current = ExpandExpressionBody(current, arrow, statements);
                continue;
            }

            if (currentAnchor.Parent is BlockSyntax block)
            {
                var anchorStatement = (StatementSyntax)currentAnchor;
                var index = block.Statements.IndexOf(anchorStatement);
                var newStatements = block.Statements
                    .Take(index)
                    .Concat(statements)
                    .Concat(block.Statements.Skip(index));
                var newBlock = block.WithStatements(
                    SyntaxFactory.List(newStatements));
                current = current.ReplaceNode(block, newBlock);
            }
            else if (currentAnchor.Parent is GlobalStatementSyntax global
                     && global.Parent is CompilationUnitSyntax unit)
            {
                // Top-level statements: the anchor lives inside a global
                // statement. Insert the lifted sequences as preceding global
                // statements (same synthesized Main scope); wrapping them in a
                // nested block would hide locals from later global statements.
                var index = unit.Members.IndexOf(global);
                var liftedGlobals = statements
                    .Select(s => (MemberDeclarationSyntax)
                        SyntaxFactory.GlobalStatement(s));
                var newMembers = unit.Members
                    .Take(index)
                    .Concat(liftedGlobals)
                    .Concat(unit.Members.Skip(index));
                var newUnit = unit.WithMembers(
                    SyntaxFactory.List(newMembers));
                current = current.ReplaceNode(unit, newUnit);
            }
            else
            {
                var anchorStatement = (StatementSyntax)currentAnchor;
                var wrapped = SyntaxFactory.Block(
                    SyntaxFactory.List(
                        statements.Concat(new[] { anchorStatement })));
                current = current.ReplaceNode(currentAnchor, wrapped);
            }
        }

        return (CompilationUnitSyntax)current;
    }

    /// <summary>
    /// Converts an expression-bodied member that needs lifted statements into
    /// a block body (or, for properties/indexers, a block-bodied get accessor).
    /// </summary>
    private static SyntaxNode ExpandExpressionBody(
        SyntaxNode current,
        ArrowExpressionClauseSyntax arrow,
        IReadOnlyList<StatementSyntax> statements)
    {
        var member = arrow.Parent!;
        var returnsValue = MemberReturnsValue(member);

        var allStatements = new List<StatementSyntax>(statements)
        {
            returnsValue
                ? SyntaxFactory.ReturnStatement(arrow.Expression)
                : SyntaxFactory.ExpressionStatement(arrow.Expression),
        };
        var block = SyntaxFactory.Block(SyntaxFactory.List(allStatements));

        SyntaxNode updated = member switch
        {
            MethodDeclarationSyntax m => m.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            LocalFunctionStatementSyntax l => l.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            OperatorDeclarationSyntax o => o.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            ConversionOperatorDeclarationSyntax c => c.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            ConstructorDeclarationSyntax ctor => ctor.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            DestructorDeclarationSyntax dtor => dtor.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            AccessorDeclarationSyntax a => a.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            PropertyDeclarationSyntax p => p.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(MakeGetAccessorList(block)),
            IndexerDeclarationSyntax i => i.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(MakeGetAccessorList(block)),
            _ => member,
        };

        return current.ReplaceNode(member, updated);
    }

    private static AccessorListSyntax MakeGetAccessorList(BlockSyntax block)
        => SyntaxFactory.AccessorList(
            SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(
                        SyntaxKind.GetAccessorDeclaration)
                    .WithBody(block)));

    private static bool MemberReturnsValue(SyntaxNode member)
        => member switch
        {
            MethodDeclarationSyntax m => !IsVoid(m.ReturnType),
            LocalFunctionStatementSyntax l => !IsVoid(l.ReturnType),
            OperatorDeclarationSyntax => true,
            ConversionOperatorDeclarationSyntax => true,
            PropertyDeclarationSyntax => true,
            IndexerDeclarationSyntax => true,
            AccessorDeclarationSyntax a =>
                !a.IsKind(SyntaxKind.SetAccessorDeclaration)
                && !a.IsKind(SyntaxKind.AddAccessorDeclaration)
                && !a.IsKind(SyntaxKind.RemoveAccessorDeclaration)
                && !a.IsKind(SyntaxKind.InitAccessorDeclaration),
            _ => false,
        };

    private static bool IsVoid(TypeSyntax returnType)
        => returnType is PredefinedTypeSyntax predefined
           && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    /// <summary>Builds <c>var name = initializer;</c>.</summary>
    internal static LocalDeclarationStatementSyntax DeclareVar(
        string name, ExpressionSyntax initializer)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(name),
                        null,
                        SyntaxFactory.EqualsValueClause(initializer)))));

    /// <summary>Wraps one temp declaration as a single-statement pending entry.</summary>
    internal static PendingTemp ForDeclaration(
        LocalDeclarationStatementSyntax declaration,
        SyntaxNode anchor,
        int order)
        => new(new[] { declaration }, anchor, order);
}

/// <summary>
/// File-scoped temp-name allocator with collision avoidance. Names use the
/// family <c>__ed_</c> prefix; existing local declarations in the file seed
/// the used-name set.
/// </summary>
internal sealed class ExpressionDesugarTempNameAllocator
{
    private readonly HashSet<string> _used;
    private int _counter;

    public ExpressionDesugarTempNameAllocator(CompilationUnitSyntax root)
    {
        _used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var local in root.DescendantNodes()
                     .OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var variable in local.Declaration.Variables)
                _used.Add(variable.Identifier.Text);
        }
    }

    /// <summary>Allocates a fresh <c>__ed_n</c> name not present in the file.</summary>
    public string Allocate()
    {
        while (true)
        {
            var name = $"__ed_{_counter}";
            _counter++;
            if (_used.Add(name))
                return name;
        }
    }
}
