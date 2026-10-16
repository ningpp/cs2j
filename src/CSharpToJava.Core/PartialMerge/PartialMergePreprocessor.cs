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
        IReadOnlyList<Action<TypeGroupPlan>>? groupMutators = null,
        Func<IReadOnlyList<Diagnostic>>? wholeTreeErrorsProvider = null)
        => new PartialMergeAnalyzer().Analyze(
            compilation, _options, sourceRoot, groupMutators, wholeTreeErrorsProvider);

    public string RewriteFile(string source, IReadOnlyList<FileEdit> edits)
        => new PartialMergeRewriter().Apply(source, edits);

    /// <summary>
    /// Multi-part groups that cannot be emitted this run because an enclosing
    /// multi-part group is inactive (rolled back / baseline-skipped /
    /// unsupported-shape / already marked enclosed). Inactivity propagates down
    /// the containing-type chain: a nested group emits no file-level edits and its
    /// merged node can only ride a merged container. Decided from the ORIGINAL
    /// inactive flags only, so the result is order-independent at any depth.
    /// </summary>
    public static IReadOnlyList<TypeGroupPlan> EnclosedByInactiveGroup(
        IReadOnlyList<TypeGroupPlan> groups)
    {
        var bySymbol = new Dictionary<INamedTypeSymbol, TypeGroupPlan>(SymbolEqualityComparer.Default);
        foreach (var group in groups)
        {
            bySymbol[group.Symbol] = group;
        }

        bool IsInactive(TypeGroupPlan group)
            => group.RolledBack || group.SkipBaseline
                || group.SkipUnsupportedShape || group.SkipEnclosingInactive;

        var blocked = new List<TypeGroupPlan>();
        foreach (var group in groups)
        {
            if (IsInactive(group))
            {
                continue;
            }

            for (var containing = group.Symbol.ContainingType;
                 containing is not null;
                 containing = containing.ContainingType)
            {
                if (bySymbol.TryGetValue(containing, out var ancestor) && IsInactive(ancestor))
                {
                    blocked.Add(group);
                    break;
                }
            }
        }

        return blocked;
    }

    /// <summary>
    /// Residual gate (design D2/§6): every multi-part source type must be merged
    /// or explicitly whitelisted. Whitelisted groups are the inactive groups
    /// (rolled back/skipped) plus nested groups enclosed by one — the latter
    /// cannot merge until their container does. The partial token itself is never
    /// residual.
    /// </summary>
    public IReadOnlyList<PartialMergeDiagnostic> CheckResidual(
        CSharpCompilation candidate,
        PartialMergePlan plan)
    {
        var whitelist = new HashSet<string>(
            plan.Groups
                .Where(g => g.RolledBack || g.SkipBaseline
                    || g.SkipUnsupportedShape || g.SkipEnclosingInactive)
                .Select(g => g.TypeFullName),
            StringComparer.Ordinal);

        // Defense in depth: even if a caller did not propagate the enclosing
        // flag, the gate is the authority for the merge/whitelist invariant.
        foreach (var blocked in EnclosedByInactiveGroup(plan.Groups))
        {
            whitelist.Add(blocked.TypeFullName);
        }

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
