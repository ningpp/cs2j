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

    /**
     * Boxed-action shape (C# Action&lt;object&gt;/Action&lt;Integer&gt; lambdas). Kept
     * under a distinct name: an overload forValue(..., Consumer&lt;Integer&gt;)
     * makes every `Parallel.For(0, n, obj::methodWithIntParam)` method reference
     * ambiguous against the IntConsumer overload.
     */
    public static void forValueBoxed(int startInclusive, int endExclusive,
                                     java.util.function.Consumer<Integer> body) {
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

    /**
     * C# Parallel.ForEach(IEnumerable&lt;T&gt;, Action&lt;T&gt;) — two-argument
     * overload without ParallelOptions.
     */
    public static <T> void forEach(Iterable<? extends T> source,
                                   java.util.function.Consumer<T> body) {
        forEach(source, null, body);
    }

    /**
     * C# Parallel.Invoke(params Action[]). Executed sequentially, matching the
     * single-threaded emulation used by the other methods here.
     */
    public static void invoke(Runnable... actions) {
        if (actions == null) {
            return;
        }
        for (Runnable action : actions) {
            action.run();
        }
    }
}
