using System.Collections.Generic;

namespace SampleForeachSourceLib;

/// <summary>
/// .NET-only behavior samples whose surface is outside the C40 Java face:
/// per-iteration lambda capture (Decision 12) exercises closure-lift and
/// Func/delegate conversion, which are owned by their own gates. Kept in the
/// Tests project so the C# runtime oracle still covers the foreach semantics
/// while the Lib project stays compilable by the C40 JVM gate.
/// </summary>
internal static class DotNetOnlySamples
{
    public static List<int> CaptureActions(PlainDuck duck)
    {
        var actions = new List<Func<int>>();
        foreach (var item in duck)
        {
            actions.Add(() => item);
        }

        var values = new List<int>();
        foreach (var action in actions)
        {
            values.Add(action());
        }

        return values;
    }
}
