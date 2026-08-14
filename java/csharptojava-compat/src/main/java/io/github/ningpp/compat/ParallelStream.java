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
}
