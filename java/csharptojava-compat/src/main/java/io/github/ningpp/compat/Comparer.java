package io.github.ningpp.compat;

import java.util.Comparator;

/** Host for C# {@code System.Collections.Generic.Comparer<T>}. */
public final class Comparer {
    private Comparer() {
    }

    /** C# {@code Comparer<T>.Default}: compares elements through their natural order. */
    @SuppressWarnings("unchecked")
    public static <T> Comparator<T> getDefault() {
        return (a, b) -> ((Comparable<T>) a).compareTo(b);
    }

    /** C# {@code Comparer<T>.Create(comparison)}: the supplied comparer is the result. */
    public static <T> Comparator<T> create(Comparator<T> comparer) {
        return comparer;
    }
}
