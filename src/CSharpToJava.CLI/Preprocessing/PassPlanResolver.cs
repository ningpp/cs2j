using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// The enabled subset of a validated registry, in locked topological order,
/// plus any CS2J9002 errors produced while resolving toggles.
/// </summary>
internal sealed class PassPlan
{
    public required PassRegistry Registry { get; init; }
    public required IReadOnlyList<PassDescriptor> Enabled { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Resolves which passes a convert-project invocation actually runs.
/// Precedence per pass: descriptor default → legacy CLI flag
/// (<c>--no-xxx</c>/enable switches) → explicit <c>--enable-pass</c>/
/// <c>--disable-pass</c> overrides. Unknown or conflicting ids fail with
/// CS2J9002 instead of being silently ignored.
/// </summary>
internal static class PassPlanResolver
{
    /// <summary>
    /// Build the execution plan.
    /// </summary>
    /// <param name="registry">Validated, topologically ordered full pass registry.</param>
    /// <param name="legacyToggles">
    /// Per-id on/off state from the historical convert-project flags. Absent ids
    /// fall back to <see cref="PassDescriptor.DefaultEnabled"/>.
    /// </param>
    /// <param name="enablePassIds">Explicit <c>--enable-pass</c> overrides (Step 7 CLI surface).</param>
    /// <param name="disablePassIds">Explicit <c>--disable-pass</c> overrides (Step 7 CLI surface).</param>
    public static PassPlan Resolve(
        PassRegistry registry,
        IReadOnlyDictionary<string, bool>? legacyToggles = null,
        IEnumerable<string>? enablePassIds = null,
        IEnumerable<string>? disablePassIds = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var errors = new List<string>();

        var explicitEnabled = Dedup(enablePassIds);
        var explicitDisabled = Dedup(disablePassIds);

        foreach (var id in explicitEnabled.Concat(explicitDisabled))
        {
            if (!registry.TryGet(id, out _))
            {
                errors.Add(
                    $"{PreprocessDiagnosticCodes.UnknownPass}: unknown preprocess pass id '{id}'.");
            }
        }

        var conflicts = explicitEnabled
            .Intersect(explicitDisabled, StringComparer.Ordinal)
            .ToList();
        foreach (var id in conflicts)
        {
            errors.Add(
                $"{PreprocessDiagnosticCodes.PassToggleConflict}: pass '{id}' cannot be both enabled and disabled.");
        }

        var enabledDescriptors = new List<PassDescriptor>();
        foreach (var descriptor in registry.Ordered)
        {
            var enabled = descriptor.DefaultEnabled;
            if (legacyToggles != null && legacyToggles.TryGetValue(descriptor.Id, out var legacyValue))
            {
                enabled = legacyValue;
            }

            if (explicitDisabled.Contains(descriptor.Id))
            {
                enabled = false;
            }

            if (explicitEnabled.Contains(descriptor.Id))
            {
                enabled = true;
            }

            if (enabled)
            {
                enabledDescriptors.Add(descriptor);
            }
        }

        return new PassPlan
        {
            Registry = registry,
            Enabled = enabledDescriptors,
            Errors = errors,
        };
    }

    private static HashSet<string> Dedup(IEnumerable<string>? ids)
        => new(ids ?? Array.Empty<string>(), StringComparer.Ordinal);
}

/// <summary>A <c>RunAfterIds</c> requirement: <see cref="PassId"/> runs after <see cref="DependencyId"/>.</summary>
internal readonly record struct PassDependency(string PassId, string DependencyId);

/// <summary>
/// Result of expanding an explicit <c>--pass</c> selection with its transitive
/// <see cref="PassDescriptor.RunAfterIds"/> prerequisites (upgrade plan §6.4-R6).
/// </summary>
internal sealed class PassDependencyExpansion
{
    /// <summary>
    /// Explicit ids plus the auto-enabled prerequisites, in locked topological
    /// order (as produced by <see cref="PassRegistry.Ordered"/>). Unknown ids are
    /// preserved so <see cref="PassPlanResolver"/> still reports CS2J9002.
    /// </summary>
    public required IReadOnlyList<string> EnabledIds { get; init; }

    /// <summary>Prerequisites that were newly auto-enabled, with the pass that required them.</summary>
    public required IReadOnlyList<PassDependency> AutoEnabled { get; init; }

    /// <summary>Exact auto-enable trace line emitted on stderr.</summary>
    public static string DescribeAutoEnabled(PassDependency dependency)
        => $"auto-enabled dependency '{dependency.DependencyId}' for '{dependency.PassId}' (RunAfterIds)";
}

/// <summary>
/// Expands an explicit pass selection into its transitive <c>RunAfterIds</c>
/// dependency closure. Missing prerequisites are always auto-enabled: the chain
/// feeds each pass' output tree to the next, so appending them in locked
/// topological order is safe with or without a unified intermediate tree.
/// </summary>
internal static class PassDependencyClosure
{
    public static PassDependencyExpansion Expand(
        PassRegistry registry,
        IEnumerable<string> explicitEnableIds)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(explicitEnableIds);

        var requested = new List<string>();
        var requestedSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in explicitEnableIds)
        {
            if (requestedSet.Add(id))
            {
                requested.Add(id);
            }
        }

        var enabledSet = new HashSet<string>(requestedSet, StringComparer.Ordinal);
        var autoEnabled = new List<PassDependency>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var recorded = new HashSet<PassDependency>();
        var queue = new Queue<string>(requested);

        while (queue.Count > 0)
        {
            var passId = queue.Dequeue();
            if (!visited.Add(passId))
            {
                continue;
            }

            if (!registry.TryGet(passId, out var descriptor))
            {
                // Unknown id: PassPlanResolver reports CS2J9002; keep the id in
                // EnabledIds so that error still surfaces.
                continue;
            }

            foreach (var dependencyId in descriptor.RunAfterIds)
            {
                var dependency = new PassDependency(passId, dependencyId);
                if (!recorded.Add(dependency))
                {
                    continue;
                }

                queue.Enqueue(dependencyId);

                if (requestedSet.Contains(dependencyId))
                {
                    continue;
                }

                if (enabledSet.Add(dependencyId))
                {
                    autoEnabled.Add(dependency);
                }
            }
        }

        // Locked topological order for the known ids; append unknown requested
        // ids afterwards so the resolver still reports them.
        var ordered = registry.Ordered
            .Select(pass => pass.Id)
            .Where(enabledSet.Contains)
            .ToList();
        var known = new HashSet<string>(ordered, StringComparer.Ordinal);
        ordered.AddRange(requested.Where(id => !known.Contains(id)));

        return new PassDependencyExpansion
        {
            EnabledIds = ordered,
            AutoEnabled = autoEnabled,
        };
    }
}
