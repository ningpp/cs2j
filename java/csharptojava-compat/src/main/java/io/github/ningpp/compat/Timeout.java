package io.github.ningpp.compat;

/**
 * Replacement for System.Threading.Timeout (converted from C#).
 * InfiniteTimeSpan equals TimeSpan.FromMilliseconds(-1) == new TimeSpan(-10000 ticks).
 */
public final class Timeout {
    /** Mirrors C# Timeout.Infinite (-1 milliseconds). */
    public static final int Infinite = -1;

    /** Mirrors C# Timeout.InfiniteTimeSpan (TimeSpan.FromMilliseconds(-1)). */
    public static final CSharpTimeSpan InfiniteTimeSpan = new CSharpTimeSpan(-10000L);

    /** Getter-style alias for converted property accesses. */
    public static CSharpTimeSpan getInfiniteTimeSpan() {
        return InfiniteTimeSpan;
    }

    private Timeout() {
    }
}
