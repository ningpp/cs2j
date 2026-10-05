namespace SampleDynamicDesugarLib;

/// <summary>
/// C25 P1 positive surface. Every <c>dynamic</c> local in this file is provably
/// concretizable to one sealed concrete type (a sealed class, <c>int</c> or
/// <c>string</c>): Rewrite mode replaces only the declaration positions and all
/// use-sites (member access, invocation, arithmetic, argument, foreach) rebind
/// statically on recompilation. This file therefore has ZERO residual dynamic
/// sites after rewrite and is deliberately absent from Cs2jDynamicAudit.json.
/// </summary>
public sealed class SealedBuilder
{
    private readonly List<string> _items = new();

    public SealedBuilder Add(string item)
    {
        _items.Add(item);
        return this;
    }

    public int Count => _items.Count;

    public string Label => "builder";
}

public static class P1Concretizations
{
    /// <summary>Sealed receiver: method chain plus member/property use-sites.</summary>
    public static string SealedReceiverChain()
    {
        dynamic builder = new SealedBuilder();
        builder.Add("a").Add("b");
        int count = builder.Count;
        string label = builder.Label;
        return label + ":" + count;
    }

    /// <summary>int operations plus the argument use-site (dynamic argument makes
    /// the otherwise static invocation late-bound before concretization).</summary>
    public static int IntArithmetic()
    {
        dynamic n = 41;
        int doubled = n * 2;
        int sum = n + 1;
        long viaArgument = AcceptLong(n);
        return doubled + sum + (int)viaArgument;
    }

    /// <summary>string operations plus the foreach-iteration use-site (the
    /// iteration variable is dynamic over a static string array).</summary>
    public static string StringArithmetic()
    {
        dynamic s = "ab";
        string concat = s.ToUpper() + s;
        foreach (dynamic c in new[] { "k1", "k2" })
        {
            concat += c.ToUpper();
        }

        return concat;
    }

    private static long AcceptLong(long value) => value * 3L;
}
