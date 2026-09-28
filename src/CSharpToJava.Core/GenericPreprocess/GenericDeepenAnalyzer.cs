using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

public enum GenericDeepenParamKind { ClassTypeParameter, MethodTypeParameter }

/// <summary>Type parameter referenced by (kind, ordinal) so family members with renamed parameters stay comparable.</summary>
public sealed record GenericDeepenParamRef(GenericDeepenParamKind Kind, int Ordinal, string Name)
    : IComparable<GenericDeepenParamRef>
{
    public int CompareTo(GenericDeepenParamRef? other)
    {
        if (other == null) return 1;
        var kind = Kind.CompareTo(other.Kind);
        return kind != 0 ? kind : Ordinal.CompareTo(other.Ordinal);
    }
}

/// <summary>Anchor plan for one method: the ordered type parameters whose runtime tokens the Java side needs.</summary>
public sealed class TokenAnchorPlan
{
    public required IMethodSymbol Method { get; init; }
    public required MethodDeclarationSyntax Declaration { get; init; }
    public required IReadOnlyList<GenericDeepenParamRef> Params { get; init; }
    /// <summary>Member without a body (abstract/interface): materialize a stub body carrying typeof statements (E10 precedent).</summary>
    public bool MaterializeBody { get; init; }
    public bool IsInterfaceMember { get; init; }
    public bool FromFamilySync { get; init; }
    public string FilePath => Declaration.SyntaxTree.FilePath;
}

/// <summary>Whole-compilation analysis result: plans keyed by symbol, sorted by (file, span).</summary>
public sealed class GenericDeepenAnalysis
{
    public required IReadOnlyList<TokenAnchorPlan> Anchors { get; init; }
    public required IReadOnlyList<GenericDeepenDiagnostic> Diagnostics { get; init; }
    public required GenericDeepenStatistics Statistics { get; init; }

    public static GenericDeepenAnalysis Empty { get; } = new()
    {
        Anchors = Array.Empty<TokenAnchorPlan>(),
        Diagnostics = Array.Empty<GenericDeepenDiagnostic>(),
        Statistics = new GenericDeepenStatistics(),
    };
}

/// <summary>
/// Whole-compilation analyzer for generic-deepen (C02): collects per-method token needs,
/// override/interface family edges and forwarding edges, then produces plans.
/// </summary>
public sealed class GenericDeepenAnalyzer
{
    private readonly Compilation _compilation;
    private readonly GenericDeepenOptions _options;

    public GenericDeepenAnalyzer(Compilation compilation, GenericDeepenOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    public GenericDeepenAnalysis Analyze() => GenericDeepenAnalysis.Empty;
}
