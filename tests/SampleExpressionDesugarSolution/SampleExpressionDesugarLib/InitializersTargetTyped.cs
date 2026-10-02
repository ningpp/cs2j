using System.Collections.Generic;

namespace SampleExpressionDesugarLib;

/// <summary>Target-typed <c>new()</c> behavior observations (bare + with initializer).</summary>
public static class TargetTypedScenarios
{
    public static string LocalInitializer()
    {
        List<int> a = new() { 1, 2 };
        return a.Count + ":" + a[0] + a[1];
    }

    public static string BareLocal()
    {
        List<int> b = new();
        b.Add(9);
        return b[0].ToString();
    }

    public static List<int> ReturnWithInitializer() => new() { 3, 4 };

    public static string NullableTarget()
    {
        List<int>? x = new() { 5 };
        return x!.Count.ToString();
    }
}
