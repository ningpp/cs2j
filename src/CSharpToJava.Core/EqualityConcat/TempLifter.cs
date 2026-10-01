using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EqualityConcat;

/// <summary>
/// Single-evaluation temp-lifting machinery for EC-4 compound assignments.
/// Pass 1 collects <see cref="PendingTemp"/> entries; <see cref="Apply"/>
/// (Pass 2) inserts the declarations before their anchor statements. Mirrors
/// the two-pass discipline of the E03/C31 temp lifters; temp names use the
/// repository-wide <c>__cs2j_</c> prefix.
/// </summary>
internal static class TempLifter
{
    internal sealed record PendingTemp(
        LocalDeclarationStatementSyntax Declaration,
        SyntaxNode AnchorNode,
        int Order);

    /// <summary>
    /// Finds the nearest enclosing statement, which serves as the insertion
    /// anchor. When the site is in an expression-bodied member, the
    /// <see cref="ArrowExpressionClauseSyntax"/> is returned (the clause is
    /// converted to a block). Returns null in truly anchor-free contexts
    /// (field/property initializers, attribute arguments, ...).
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
    /// Inserts all collected declarations. When the anchor is a direct
    /// statement of a <see cref="BlockSyntax"/>, declarations are inserted
    /// before it; when the anchor is an embedded statement (if/while/...) or
    /// a switch-section statement, the anchor is wrapped in a block carrying
    /// the declarations, keeping scope and evaluation order intact.
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
                    .Select(p => p.Declaration)
                    .ToList());

        SyntaxNode current = root;
        foreach (var (anchor, declarations) in byAnchor)
        {
            if (!anchorAnnotations.TryGetValue(anchor, out var annotation))
                continue;

            var currentAnchor = current.GetAnnotatedNodes(annotation)
                .FirstOrDefault();
            if (currentAnchor == null)
                continue;

            if (currentAnchor is ArrowExpressionClauseSyntax arrow)
            {
                current = ExpandExpressionBody(current, arrow, declarations);
                continue;
            }

            if (currentAnchor.Parent is BlockSyntax block)
            {
                var anchorStatement = (StatementSyntax)currentAnchor;
                var statements = block.Statements;
                var index = statements.IndexOf(anchorStatement);
                var newStatements = statements
                    .Take(index)
                    .Concat(declarations)
                    .Concat(statements.Skip(index));
                var newBlock = block.WithStatements(
                    SyntaxFactory.List(newStatements));
                current = current.ReplaceNode(block, newBlock);
            }
            else
            {
                var anchorStatement = (StatementSyntax)currentAnchor;
                var wrapped = SyntaxFactory.Block(
                    SyntaxFactory.List(
                        declarations.Concat(new[] { anchorStatement })));
                current = current.ReplaceNode(currentAnchor, wrapped);
            }
        }

        return (CompilationUnitSyntax)current;
    }

    /// <summary>
    /// Converts an expression-bodied member that needs temps into a block
    /// body (or, for properties/indexers, a block-bodied get accessor).
    /// </summary>
    private static SyntaxNode ExpandExpressionBody(
        SyntaxNode current,
        ArrowExpressionClauseSyntax arrow,
        IReadOnlyList<StatementSyntax> declarations)
    {
        var member = arrow.Parent!;
        var returnsValue = MemberReturnsValue(member);

        var statements = new List<StatementSyntax>(declarations);
        statements.Add(returnsValue
            ? SyntaxFactory.ReturnStatement(arrow.Expression)
            : SyntaxFactory.ExpressionStatement(arrow.Expression));
        var block = SyntaxFactory.Block(SyntaxFactory.List(statements));

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
}

/// <summary>File-scoped temp-name allocator with collision avoidance.</summary>
internal sealed class EqualityConcatTempNameAllocator
{
    private readonly HashSet<string> _used;
    private int _counter;

    public EqualityConcatTempNameAllocator(CompilationUnitSyntax root)
    {
        _used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var local in root.DescendantNodes()
                     .OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var variable in local.Declaration.Variables)
                _used.Add(variable.Identifier.Text);
        }
    }

    /// <summary>Allocates a fresh <c>__cs2j_ec&lt;n&gt;</c> name.</summary>
    public string Allocate()
    {
        while (true)
        {
            _counter++;
            var name = $"__cs2j_ec{_counter}";
            if (_used.Add(name))
                return name;
        }
    }
}
