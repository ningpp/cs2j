namespace SampleExtLib.Spans;

/// <summary>
/// C19 M2: extensions with <c>ref</c>/<c>scoped ref</c> receivers over
/// <see cref="Span{T}"/>. The pass must emit the <c>ref</c> keyword at the
/// static call site; <c>scoped</c> is metadata-only.
/// </summary>
public static class SpanExtensions
{
    public static void IncrementFirst(this ref Span<int> span)
    {
        if (span.Length > 0)
        {
            span[0]++;
        }
    }

    public static int Sum(this scoped ref Span<int> span)
    {
        var total = 0;
        for (var i = 0; i < span.Length; i++)
        {
            total += span[i];
        }

        return total;
    }
}
