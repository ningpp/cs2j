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

    public static bool RequiresManifest(string profile)
        => profile is "java-ready" or "audit";

    public static IReadOnlyList<JcsCapabilityId> RequiredCapabilitiesFor(string profile)
        => RequiresManifest(profile)
            ? JavaReadyCapabilities
            : Array.Empty<JcsCapabilityId>();
}
