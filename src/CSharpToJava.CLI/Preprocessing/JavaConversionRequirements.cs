using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// R12 single source of truth for the capabilities a Java conversion profile
/// requires from the preprocessed tree. Kept on the CLI side as the future
/// Java-side preprocess mirror.
/// </summary>
internal static class JavaConversionRequirements
{
    private static readonly JcsCapabilityId[] JavaReadyCapabilities =
    {
        JcsCapabilityId.Workspace,
        JcsCapabilityId.Symbol,
        JcsCapabilityId.Conversion,
    };

    /// <summary>
    /// JM-IDs the registry counts as retired (<see cref="JmRetirementState.Retire"/>
    /// or <see cref="JmRetirementState.Ratchet"/>), ordinal-sorted. The first cut
    /// only contains the ratchet guard; no implementation branch is in Retire
    /// state yet, so no runtime gate consumes this list. Wiring the
    /// <c>--no-preprocess</c> retired-hit gate (CS2J1410) is deferred to a later
    /// wave.
    /// </summary>
    public static IReadOnlyList<string> RetiredJmIds { get; } = JmRegistry.Default
        .Where(entry => JmRegistry.IsRetired(entry.RetirementState))
        .Select(entry => entry.JmId)
        .OrderBy(jmId => jmId, StringComparer.Ordinal)
        .ToArray();

    public static bool IsJmRetired(string jmId)
        => RetiredJmIds.Contains(jmId, StringComparer.Ordinal);

    /// <summary>
    /// The JCS capability a JM-ID depends on, or null for an unknown JM-ID.
    /// </summary>
    public static JcsCapabilityId? RequiredCapabilityForJm(string jmId)
    {
        var entry = JmRegistry.Default.FirstOrDefault(candidate =>
            string.Equals(candidate.JmId, jmId, StringComparison.Ordinal));
        return entry is not null
            && JcsCapabilityIds.TryParse(entry.RequiredCapability, out var capability)
            ? capability
            : null;
    }

    public static bool RequiresManifest(string profile)
        => profile is "java-ready" or "audit";

    public static IReadOnlyList<JcsCapabilityId> RequiredCapabilitiesFor(string profile)
        => RequiresManifest(profile)
            ? JavaReadyCapabilities
            : Array.Empty<JcsCapabilityId>();
}
