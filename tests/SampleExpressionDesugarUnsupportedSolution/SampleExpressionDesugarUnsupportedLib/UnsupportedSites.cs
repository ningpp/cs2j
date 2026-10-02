using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SampleExpressionDesugarUnsupportedLib;

/// <summary>
/// Declares a CollectionBuilder whose factory signature is unusable for the
/// collection-expression pipeline: the only <c>Create</c> takes a single int,
/// not a ReadOnlySpan&lt;T&gt;/T[] argument. CE-10 cannot resolve a unique
/// factory -> CS2J0580 (baseline CS9187 territory).
/// </summary>
[CollectionBuilder(typeof(BadBuiltBuilder), "Create")]
public sealed class BadBuilt : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() =>
        ((IEnumerable<int>)System.Array.Empty<int>()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => System.Array.Empty<int>().GetEnumerator();
}

public static class BadBuiltBuilder
{
    public static BadBuilt Create(int marker) => new();
}

/// <summary>
/// Targets the C36 M2 rules cannot lower: bad builder factory and the
/// non-generic IEnumerable / IDictionary targets that baseline C# itself
/// rejects with CS9174.
/// </summary>
public static class UnsupportedSites
{
    public static BadBuilt BadFactory() => [1, 2];

    public static void BaselineTableOut(
        System.Collections.Generic.IDictionary<int, string> dictionary,
        IEnumerable nonGeneric)
    {
        dictionary = [new KeyValuePair<int, string>(1, "x")];
        nonGeneric = [1];
    }
}
