package io.github.ningpp.compat;

import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * System.Threading.CancellationTokenSource compatibility class.
 *
 * <p>Owns one {@link CancellationToken}; {@link #cancel()} performs the one-way transition
 * and fires the token's registered callbacks. {@link #cancelAfter(long)} schedules the
 * transition on a shared daemon scheduler (timer granularity is OS-scheduler dependent).</p>
 */
public class CancellationTokenSource implements AutoCloseable {

    /** Shared daemon scheduler for {@link #cancelAfter(long)}; never keeps the JVM alive. */
    private static final ScheduledExecutorService SCHEDULER;

    static {
        ThreadFactory factory = runnable -> {
            Thread thread = new Thread(runnable, "cs2j-cancellation-timer");
            thread.setDaemon(true);
            return thread;
        };
        SCHEDULER = Executors.newScheduledThreadPool(1, factory);
    }

    private final CancellationToken token = new CancellationToken();
    private final AtomicBoolean cancelled = new AtomicBoolean(false);
    private volatile ScheduledFuture<?> scheduledCancel;

    public CancellationToken getToken() {
        return token;
    }

    /** C# {@code Cancel()}: transitions the token and fires callbacks exactly once. */
    public void cancel() {
        if (cancelled.compareAndSet(false, true)) {
            token.signalCancel();
        }
    }

    /** C# {@code CancelAfter(int)} / {@code CancelAfter(TimeSpan)}: schedules {@link #cancel()}. */
    public void cancelAfter(int millisecondsDelay) {
        cancelAfter((long) millisecondsDelay);
    }

    public void cancelAfter(long millisecondsDelay) {
        if (millisecondsDelay < 0) {
            throw new ArgumentOutOfRangeException(
                "millisecondsDelay", "Delay must be non-negative.");
        }
        if (cancelled.get()) {
            return;
        }
        scheduledCancel = SCHEDULER.schedule(this::cancel, millisecondsDelay, TimeUnit.MILLISECONDS);
    }

    public boolean isCancellationRequested() {
        return cancelled.get();
    }

    public boolean getIsCancellationRequested() {
        return isCancellationRequested();
    }

    /** C# {@code Dispose()}: cancels a pending timer; an already-cancelled source is unaffected. */
    @Override
    public void close() {
        ScheduledFuture<?> pending = scheduledCancel;
        if (pending != null) {
            pending.cancel(false);
        }
    }
}
