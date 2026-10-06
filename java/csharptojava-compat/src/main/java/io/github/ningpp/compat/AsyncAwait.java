package io.github.ningpp.compat;

import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutionException;

/**
 * Bridge for the C# {@code await} operator on {@code Task}/{@code ValueTask}
 * (mapped to {@link CompletableFuture}).
 *
 * <p>The converter emits {@code AsyncAwait.join(future)} instead of a raw {@code future.join()}.
 * {@link CompletableFuture#join()} wraps every failure in a {@link CompletionException}, but a
 * C# {@code await} rethrows the original exception, so user {@code catch} clauses (and xUnit's
 * {@code ThrowsAsync}) must see the cause directly. This bridge unwraps
 * {@link CompletionException}/{@link ExecutionException} and sneaky-rethrows the cause,
 * preserving checked-exception types (matching C#'s lack of a throws clause).</p>
 *
 * <p>Blocking semantics: this lightweight bridge waits on the calling thread just like
 * {@code join()}; it fixes the exception contract but is not a state-machine scheduler.</p>
 */
public final class AsyncAwait {

    private AsyncAwait() {
    }

    /** Awaits the future and returns its result, throwing the unwrapped cause on failure. */
    public static <T> T join(CompletableFuture<T> future) {
        try {
            return future.join();
        } catch (CompletionException e) {
            throwUnchecked(e.getCause() != null ? e.getCause() : e);
            return null; // unreachable: throwUnchecked always throws
        }
    }

    /** Same unwrap contract for {@code get()}-style failures (checked ExecutionException). */
    public static <T> T get(java.util.concurrent.Future<T> future) throws InterruptedException {
        try {
            return future.get();
        } catch (ExecutionException e) {
            throwUnchecked(e.getCause() != null ? e.getCause() : e);
            return null; // unreachable
        }
    }

    @SuppressWarnings("unchecked")
    private static <E extends Throwable> void throwUnchecked(Throwable throwable) throws E {
        throw (E) throwable;
    }
}
