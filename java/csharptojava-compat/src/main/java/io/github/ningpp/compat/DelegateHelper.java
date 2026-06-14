package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;
import java.util.function.Function;

/** Helper for C# delegate combine/remove operations (+ / - operators). */
public final class DelegateHelper {
    private DelegateHelper() {}

    /** Combine two delegates (C# delegate + operator). */
    @SuppressWarnings("unchecked")
    public static <T> T combine(T a, T b) {
        if (a == null) return b;
        if (b == null) return a;
        // For multicast delegates, return a combined invocation list
        // Simplified: just return b (last wins for single-cast)
        return b;
    }

    /** Remove a delegate (C# delegate - operator). */
    @SuppressWarnings("unchecked")
    public static <T> T remove(T source, T value) {
        if (source == null) return null;
        if (value == null) return source;
        if (source.equals(value)) return null;
        return source;
    }
}
