using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.ThreadStaticRewrite;

public enum ThreadStaticFieldKind { Static, Instance }

public enum ThreadStaticInitializerKind { None, Inline }

public enum ThreadStaticSiteKind { Read, Write, Compound, Increment, StructMutation, RefOut }

/// <summary>One syntax node bound to a [ThreadStatic] field plan (the whole access expression).</summary>
public sealed class ReferenceSite
{
    public ReferenceSite(SyntaxNode node, ThreadStaticSiteKind kind, string filePath, TextSpan span)
    {
        Node = node;
        Kind = kind;
        FilePath = filePath;
        Span = span;
    }

    /// <summary>The bound field-access expression (IdentifierName / MemberAccess / QualifiedName).</summary>
    public SyntaxNode Node { get; }

    public ThreadStaticSiteKind Kind { get; }

    public string FilePath { get; }

    public TextSpan Span { get; }

    public int SpanStart => Span.Start;
}

/// <summary>
/// Rewrite plan for one [ThreadStatic] field. The field is the atomic rollback unit:
/// a single infeasible site deactivates the whole plan (declaration and every site stay untouched).
/// </summary>
public sealed class ThreadStaticFieldPlan : IComparable<ThreadStaticFieldPlan>
{
    public required IFieldSymbol Field { get; init; }
    public required FieldDeclarationSyntax Declaration { get; init; }
    public required VariableDeclaratorSyntax Declarator { get; init; }

    public required ThreadStaticFieldKind Kind { get; init; }
    public required ThreadStaticInitializerKind InitializerKind { get; init; }

    /// <summary>All cross-file reference sites, deterministically ordered (file path + span).</summary>
    public required IReadOnlyList<ReferenceSite> Sites { get; init; }

    /// <summary>true = rewrite declaration and sites; false = skip the whole field.</summary>
    public bool Active { get; set; } = true;

    public string? FailCode { get; set; }
    public string? FailReason { get; set; }

    public required IReadOnlyCollection<string> TouchedFiles { get; init; }

    /// <summary>Stable identifier used by ExcludePlans (overrides/rollback ownership).</summary>
    public string Id => Field.ToDisplayString();

    /// <summary>True when the changed span in <paramref name="file"/> belongs to this field's declaration or one of its sites.</summary>
    public bool OwnsTouch(string file, TextSpan span)
    {
        if (file == Declaration.SyntaxTree.FilePath && Declaration.Span.Contains(span))
        {
            return true;
        }

        foreach (var site in Sites)
        {
            if (site.FilePath == file && site.Span.Contains(span))
            {
                return true;
            }
        }

        return false;
    }

    public int CompareTo(ThreadStaticFieldPlan? other)
    {
        if (other == null) return 1;
        var fileA = Declaration.SyntaxTree.FilePath;
        var fileB = other.Declaration.SyntaxTree.FilePath;
        var byFile = string.CompareOrdinal(fileA, fileB);
        return byFile != 0 ? byFile : Declarator.SpanStart.CompareTo(other.Declarator.SpanStart);
    }
}
