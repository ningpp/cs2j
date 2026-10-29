package io.github.ningpp.compat;

import java.util.function.IntConsumer;

/**
 * Minimal compat for System.Threading.Tasks.Parallel.For(int, int, Action&lt;int&gt;).
 */
public class ParallelStream {
    private ParallelStream() {
    }

    public static void forValue(int startInclusive, int endExclusive, IntConsumer body) {
        for (int i = startInclusive; i < endExclusive; i++) {
            body.accept(i);
        }
    }

    public static void forValue(int startInclusive, int endExclusive, java.util.function.Consumer<Integer> body) {
        for (int i = startInclusive; i < endExclusive; i++) {
            body.accept(i);
        }
    }

    /**
     * C# Parallel.ForEach(IEnumerable&lt;T&gt;, ParallelOptions, Action&lt;T&gt;).
     * Generated compat collections erase to Iterable&lt;Object&gt;, so a typed
     * Consumer cannot bind through the generic Iterable signature; this view
     * accepts the erased pair and casts at the element boundary.
     */
    @SuppressWarnings("unchecked")
    public static void forEach(Iterable<?> source, Object options, java.util.function.Consumer<?> body) {
        java.util.function.Consumer<Object> consumer = (java.util.function.Consumer<Object>) body;
        for (Object item : source) {
            consumer.accept(item);
        }
    }
}
