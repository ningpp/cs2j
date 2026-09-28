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
                $"{PreprocessDiagnosticCodes.UnknownPass}: pass '{id}' cannot be both enabled and disabled.");
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
