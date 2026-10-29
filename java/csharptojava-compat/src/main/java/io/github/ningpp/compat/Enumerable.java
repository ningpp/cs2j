package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;

/**
 * C# System.Linq.Enumerable static entry points that the procedural LINQ lowering
 * does not handle (currently Repeat; Range is lowered to a Java IntStream).
 */
public final class Enumerable {
    private Enumerable() {
    }

    /** C# Enumerable.Empty&lt;T&gt;(). */
    public static <T> CSharpGenericIterable<T> empty() {
        return CSharpGenericIterable.fromCollection(new CSharpList<>());
    }

    /** C# Enumerable.Repeat&lt;T&gt;(element, count) — materialized; null/negative count is invalid in C#. */
    public static <T> CSharpGenericIterable<T> repeat(T element, int count) {
        if (count < 0) {
            throw new ArgumentOutOfRangeException("count");
        }
        CSharpList<T> list = new CSharpList<>();
        for (int i = 0; i < count; i++) {
            list.add(element);
        }
        return list;
    }

    /** C# Enumerable.Range(start, count). */
    public static CSharpGenericIterable<Integer> range(int start, int count) {
        if (count < 0) {
            throw new ArgumentOutOfRangeException("count");
        }
        List<Integer> list = new ArrayList<>(count);
        for (int i = 0; i < count; i++) {
            list.add(start + i);
        }
        return CSharpGenericIterable.from(list);
    }
}
