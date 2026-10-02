using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Facade used by the CLI adapter: Analyze once, RewriteFile per file,
/// CheckResidual after the candidate tree is built (design §4.1 / §6).
/// Internal: the plan model is an implementation detail of the pass.
/// </summary>
internal sealed class PartialMergePreprocessor
{
    private readonly PartialMergeOptions _options;

    public PartialMergePreprocessor(PartialMergeOptions? options = null)
    {
        _options = options ?? new PartialMergeOptions();
    }

    public PartialMergePlan Analyze(
        CSharpCompilation compilation,
        string sourceRoot,
        IReadOnlyList<Action<TypeGroupPlan>>? groupMutators = null)
        => new PartialMergeAnalyzer().Analyze(compilation, _options, sourceRoot, groupMutators);

    public string RewriteFile(string source, IReadOnlyList<FileEdit> edits)
        => new PartialMergeRewriter().Apply(source, edits);

    /// <summary>
    /// Residual gate (design D2/§6): every multi-part source type must belong to a
    /// rolled-back/skipped group. The partial token itself is never residual.
    /// </summary>
    public IReadOnlyList<PartialMergeDiagnostic> CheckResidual(
        CSharpCompilation candidate,
        PartialMergePlan plan)
    {
        var whitelist = new HashSet<string>(
            plan.Groups
                .Where(g => g.RolledBack || g.SkipBaseline || g.SkipUnsupportedShape)
                .Select(g => g.TypeFullName),
            StringComparer.Ordinal);

        var diagnostics = new List<PartialMergeDiagnostic>();
        foreach (var type in candidate.GlobalNamespace.GetAllTypes(candidate))
        {
            if (type.DeclaringSyntaxReferences.Length < 2 || whitelist.Contains(type.ToDisplayString()))
            {
                continue;
            }

            var severity = _options.Strict ? PartialMergeSeverity.Error : PartialMergeSeverity.Warning;
            diagnostics.Add(new PartialMergeDiagnostic(
                severity,
                PartialMergeCodes.Residual,
                "residual multi-part type '" + type.ToDisplayString() +
                "' outside the rollback/skip whitelist",
                TypeFullName: type.ToDisplayString()));
        }

        return diagnostics;
    }
}

internal static class PartialMergeSymbolExtensions
{
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(
        this INamespaceSymbol root,
        Compilation compilation)
    {
        foreach (var type in root.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in GetNestedTypes(type))
            {
                yield return nested;
            }
        }

        foreach (var nestedNamespace in root.GetNamespaceMembers())
        {
            foreach (var type in nestedNamespace.GetAllTypes(compilation))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in GetNestedTypes(nested))
            {
                yield return deeper;
            }
        }
    }
}
