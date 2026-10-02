using System.Collections.Generic;

namespace SampleExpressionDesugarLib;

/// <summary>Interface targets in field/property positions too (CE-4).</summary>
public sealed class InterfaceHolder
{
    public IEnumerable<int> EnumerableField = [10, 20];

    public IReadOnlyCollection<string> ReadOnlyCollectionProperty { get; } = ["x", "y"];
}

/// <summary>
/// All five constructible interface targets lower to a zero-temp
/// <c>new global::System.Collections.Generic.List&lt;T&gt;(){…}</c>.
/// </summary>
public static class InterfaceTargets
{
    public static IEnumerable<int> AsEnumerable() => [1, 2, 3];

    public static ICollection<int> AsCollection() => [4, 5];

    public static IList<int> AsList() => [6];

    public static IReadOnlyCollection<int> AsReadOnlyCollection() => [7, 8];

    public static IReadOnlyList<int> AsReadOnlyList() => [9, 10, 11];

    public static IEnumerable<int> SpreadToEnumerable(IEnumerable<int> first, int last)
        => [.. first, last];

    public static int Sum(IEnumerable<int> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }
}
