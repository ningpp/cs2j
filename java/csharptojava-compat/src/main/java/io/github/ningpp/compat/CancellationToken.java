package io.github.ningpp.compat;

import java.util.List;
import java.util.Objects;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * System.Threading.CancellationToken compatibility class.
 *
 * <p>Honest cooperative-cancellation token: {@link CancellationTokenSource#cancel()} flips
 * {@link #isCancellationRequested()} and synchronously runs all registered callbacks. The
 * shared {@link #getNone()} token can never be cancelled and {@link #register(Runnable)} on it
 * keeps the callback inert forever (matching C# {@code CancellationToken.None}).</p>
 *
 * <p>Contract approximation vs .NET: callbacks registered after cancellation run immediately
 * on the registering thread (the BCL may queue them, but observes-before ordering is the same);
 * callback exceptions propagate to the canceling thread.</p>
 */
public class CancellationToken {

    /** The shared token that can never be cancelled (C# CancellationToken.None). */
    private static final CancellationToken NONE = new NeverCancellableToken();

    private final AtomicBoolean cancelled = new AtomicBoolean(false);
    private final CopyOnWriteArrayList<CancellationTokenRegistration> registrations =
        new CopyOnWriteArrayList<>();

    /** Package-private: tokens are minted by CancellationTokenSource or via {@link #getNone()}. */
    CancellationToken() {
    }

    /**
     * C# CancellationToken.None — a token that can never be cancelled.
     *
     * @return The shared non-cancellable token.
     */
    public static CancellationToken getNone() {
        return NONE;
    }

    public boolean isCancellationRequested() {
        return cancelled.get();
    }

    public boolean getIsCancellationRequested() {
        return isCancellationRequested();
    }

    /**
     * C# {@code Register(Action)}: schedules {@code callback} to run once when the token is
     * cancelled. If the token is already cancelled the callback runs immediately.
     * Dispose the returned registration to detach the callback before cancellation.
     */
    public CancellationTokenRegistration register(Runnable callback) {
        Objects.requireNonNull(callback, "callback");
        var registration = new CancellationTokenRegistration(this, callback);

        if (cancelled.get()) {
            registration.fire();
            return registration;
        }

        registrations.add(registration);
        // Re-check after publish: cancel() may have completed between the fast check and add.
        if (cancelled.get()) {
            registrations.remove(registration);
            registration.fire();
        }
        return registration;
    }

    /**
     * Transitions to cancelled once; returns true on the winning call. Fires registrations
     * in registration order on the calling thread.
     */
    boolean signalCancel() {
        if (!cancelled.compareAndSet(false, true)) {
            return false;
        }
        for (var registration : List.copyOf(registrations)) {
            registration.fire();
        }
        registrations.clear();
        return true;
    }

    void removeRegistration(CancellationTokenRegistration registration) {
        registrations.remove(registration);
    }

    private static final class NeverCancellableToken extends CancellationToken {
        @Override
        public boolean isCancellationRequested() {
            return false;
        }

        @Override
        public CancellationTokenRegistration register(Runnable callback) {
            Objects.requireNonNull(callback, "callback");
            return new CancellationTokenRegistration(this, callback);
        }

        @Override
        boolean signalCancel() {
            return false;
        }

        @Override
        void removeRegistration(CancellationTokenRegistration registration) {
        }
    }
}
