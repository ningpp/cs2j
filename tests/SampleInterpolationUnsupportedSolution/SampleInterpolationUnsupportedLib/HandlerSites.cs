using System.Runtime.CompilerServices;

namespace SampleInterpolationUnsupportedLib;

/// <summary>
/// Legal C# custom interpolated-string-handler API (natively builds and
/// runs). C23 cannot emulate handlers (Q1: Error/Keep only), so with
/// --interpolation-expand the default strict policy reports CS2J0510 and
/// atomically preserves this file.
/// </summary>
public static class JsonLogger
{
    public static void Log(JsonStringHandler handler)
    {
    }
}

[InterpolatedStringHandler]
public ref struct JsonStringHandler
{
    public JsonStringHandler(int literalLength, int formattedCount, out bool append)
    {
        append = true;
    }

    public void AppendLiteral(string s)
    {
    }

    public void AppendFormatted<T>(T value)
    {
    }
}

public static class HandlerCallSites
{
    public static void Emit()
    {
        JsonLogger.Log($"id={42}");
    }
}
