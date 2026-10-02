using System;

namespace SampleExpressionDesugarLib;

/// <summary>
/// Span targets (CE-9): every site lowers to a heap array; the pass reports
/// CS2J0578 (Info) per site. Baseline C# forbids span collection expressions
/// from escaping a method (CS9203), so the legal sites are locals and
/// arguments; spreads use the lifted List temp + ToArray().
/// </summary>
public static class SpanTargets
{
    public static int MutateAndSum()
    {
        Span<int> values = [1, 2, 3];
        values[0] += 10;
        return Sum(values);
    }

    public static int EmptyBytesLength()
    {
        ReadOnlySpan<byte> values = [];
        return values.Length;
    }

    public static int LiteralArgument() => Sum([1, 2, 3]);

    public static int WithSpreadSum(int[] prefix, int tail)
    {
        ReadOnlySpan<int> values = [.. prefix, tail];
        return Sum(values);
    }

    public static int Sum(ReadOnlySpan<int> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }
}
