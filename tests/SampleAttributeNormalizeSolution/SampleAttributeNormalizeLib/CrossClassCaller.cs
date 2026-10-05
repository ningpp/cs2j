using System.Runtime.CompilerServices;

namespace SampleAttributeNormalizeLib;

public sealed class CrossClassCaller
{
    public void CallAcross(ICallerLog log)
    {
        log.Notify("cross-class site");
        new ExternalLoggingApi().Emit("external-api site");
    }
}

/// <summary>
/// Stand-in for a logging API declared in an external library. M1 has no
/// prebuilt external assembly inside this fixture, so the true external-target
/// (out-of-source declaration) path is exercised by the Core unit tests; the
/// call-site filling shape is identical for source declarations.
/// </summary>
public sealed class ExternalLoggingApi
{
    public void Emit(string message, [CallerMemberName] string member = "")
    {
    }
}
