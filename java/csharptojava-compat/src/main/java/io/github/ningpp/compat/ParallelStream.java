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
     * Keep the type parameter tied between source and body so method references
     * and implicit lambdas both infer the element type (a wildcard Consumer
     * target rejects method references).
     */
    public static <T> void forEach(Iterable<? extends T> source, Object options,
                                   java.util.function.Consumer<T> body) {
        for (T item : source) {
            body.accept(item);
        }
    }
}
