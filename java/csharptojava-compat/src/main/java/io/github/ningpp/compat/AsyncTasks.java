package io.github.ningpp.compat;

import java.util.Objects;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;

/**
 * Honest replacements for the {@code System.Threading.Tasks.Task} delay APIs.
 *
 * <p>The generated code maps {@code Task} to {@link CompletableFuture}; the BCL
 * {@code CompletableFuture.delayedExecutor(...)} only returns an {@link java.util.concurrent.Executor}
 * and therefore cannot stand in for {@code Task.Delay} (the old mapping emitted code that did
 * not compile). These helpers return a real {@code CompletableFuture<Void>} that completes
 * after the requested delay, and the token-aware overloads translate cooperative
 * cancellation into {@link java.util.concurrent.CancellationException} (the Java mapping
 * target of {@code System.OperationCanceledException}).</p>
 *
 * <p>Timing is scheduler-granularity (same caveats as {@code Task.Delay}); the scheduler uses
 * daemon threads and never keeps the JVM alive.</p>
 */
public final class AsyncTasks {

    private static final ScheduledExecutorService SCHEDULER;

    static {
        ThreadFactory factory = runnable -> {
            Thread thread = new Thread(runnable, "cs2j-async-delay");
            thread.setDaemon(true);
            return thread;
        };
        SCHEDULER = Executors.newScheduledThreadPool(1, factory);
    }

    private AsyncTasks() {
    }

    /** Replacement for {@code Task.Delay(int millisecondsDelay)}; {@code -1} means infinite. */
    public static CompletableFuture<Void> delay(long millisecondsDelay) {
        if (millisecondsDelay < -1) {
            throw new ArgumentOutOfRangeException(
                "millisecondsDelay", "Delay must be non-negative, or -1 for infinite.");
        }
        CompletableFuture<Void> future = new CompletableFuture<>();
        if (millisecondsDelay != -1) {
            SCHEDULER.schedule(
                () -> future.complete(null), millisecondsDelay, TimeUnit.MILLISECONDS);
        }
        return future;
    }

    /** Replacement for {@code Task.Delay(int, CancellationToken)} / {@code Task.Delay(TimeSpan, CancellationToken)}. */
    public static CompletableFuture<Void> delay(long millisecondsDelay, CancellationToken token) {
        Objects.requireNonNull(token, "cancellationToken");
        if (millisecondsDelay < -1) {
            throw new ArgumentOutOfRangeException(
                "millisecondsDelay", "Delay must be non-negative, or -1 for infinite.");
        }

        CompletableFuture<Void> future = new CompletableFuture<>();
        if (token.isCancellationRequested()) {
            future.cancel(false);
            return future;
        }

        ScheduledFuture<?> scheduled = millisecondsDelay == -1
            ? null
            : SCHEDULER.schedule(
                () -> future.complete(null), millisecondsDelay, TimeUnit.MILLISECONDS);
        CancellationTokenRegistration registration = token.register(() -> {
            if (scheduled != null) {
                scheduled.cancel(false);
            }
            future.cancel(false);
        });
        // Detach the cancellation callback once the delay settles either way.
        future.whenComplete((result, error) -> registration.close());
        return future;
    }

    /** Replacement for {@code Task.Delay(TimeSpan)}. */
    public static CompletableFuture<Void> delay(CSharpTimeSpan delay) {
        Objects.requireNonNull(delay, "delay");
        return delay((long) delay.getTotalMilliseconds());
    }

    /** Replacement for {@code Task.Delay(TimeSpan, CancellationToken)}. */
    public static CompletableFuture<Void> delay(CSharpTimeSpan delay, CancellationToken token) {
        Objects.requireNonNull(delay, "delay");
        return delay((long) delay.getTotalMilliseconds(), token);
    }
}
