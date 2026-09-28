namespace CSharpToJava.Core.Preprocessing;

/// <summary>
/// Shared diagnostic codes emitted by the unified preprocess infrastructure.
/// </summary>
public static class PreprocessDiagnosticCodes
{
    /// <summary>
    /// A pass registered as a <see cref="PassStatus.Stub"/> placeholder was explicitly
    /// enabled. Fails loudly instead of silently producing unprocessed output.
    /// </summary>
    public const string StubNotImplemented = "CS2J9001";

    /// <summary>Unknown pass id supplied via <c>--enable-pass</c> / <c>--disable-pass</c> / <c>--pass</c>.</summary>
    public const string UnknownPass = "CS2J9002";
}
