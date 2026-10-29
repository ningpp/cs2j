package io.github.ningpp.compat;

/**
 * C# System.Threading.Tasks.Parallel.Invoke(Action[]): method-group target used
 * when the converter stores Parallel.Invoke as a delegate. The regular call
 * lowering uses {@link ParallelStream}.
 */
public final class Parallel {
    private Parallel() {
    }

    public static void invoke(Runnable... actions) {
        for (Runnable action : actions) {
            action.run();
        }
    }
}
