using System;
using System.Collections.Generic;

namespace SampleForeachSourceLib;

// =====================================================================
// C40 foreach-source M2 FA-5 fixture: plain foreach over Span<T> /
// ReadOnlySpan<T> lowers to an index loop (cached source + int index +
// Length + indexer), never to GetEnumerator/MoveNext/Current.
// =====================================================================
public static class SpanIndexLoopSamples
{
    public static int SumSpan(Span<int> values)
    {
        var sum = 0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum;
    }

    public static int SumReadOnlySpan(ReadOnlySpan<int> values)
    {
        var sum = 0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum;
    }

    /// <summary>continue skips negatives; the 0 sentinel breaks the loop.</summary>
    public static int AccumulatePositiveUntilSentinel(Span<int> values)
    {
        var sum = 0;
        foreach (var value in values)
        {
            if (value == 0)
            {
                break;
            }

            if (value < 0)
            {
                continue;
            }

            sum += value;
        }

        return sum;
    }

    /// <summary>
    /// Nested site: outer span index loop, inner stateful struct duck while
    /// loop. Exercises the child-first rewrite ordering (M2 OriginalNode fix).
    /// </summary>
    public static List<int> NestedSpanOuterDuckInner(
        Span<int> values, StatefulStructDuck duck)
    {
        var result = new List<int>();
        foreach (var value in values)
        {
            foreach (var item in duck)
            {
                result.Add(value * 10 + item);
            }
        }

        return result;
    }
}
