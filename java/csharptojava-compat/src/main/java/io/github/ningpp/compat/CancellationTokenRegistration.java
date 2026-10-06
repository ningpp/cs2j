package io.github.ningpp.compat;

import java.util.concurrent.atomic.AtomicBoolean;

/**
 * System.Threading.CancellationTokenRegistration compatibility class.
 *
 * <p>A registration returned by {@link CancellationToken#register(Runnable)}; disposing it
 * detaches the callback so it will not run on a later cancellation. When cancellation races
 * with disposal, the callback runs at most once (whichever side wins the CAS).</p>
 */
public final class CancellationTokenRegistration implements AutoCloseable {

    private final CancellationToken token;
    private final Runnable callback;
    private final AtomicBoolean fired = new AtomicBoolean(false);

    CancellationTokenRegistration(CancellationToken token, Runnable callback) {
        this.token = token;
        this.callback = callback;
    }

    /** Fires the registered callback exactly once. Invoked by the owning token on cancel. */
    void fire() {
        if (fired.compareAndSet(false, true)) {
            callback.run();
        }
    }

    /** C# {@code Dispose()}: unregisters the callback. Safe to call repeatedly. */
    public void dispose() {
        if (fired.compareAndSet(false, true)) {
            token.removeRegistration(this);
        }
    }

    /** Alias kept for the {@code using}/{@code AutoCloseable} shape emitted by the converter. */
    @Override
    public void close() {
        dispose();
    }

    public boolean getIsDisposed() {
        return fired.get();
    }

    @Override
    public boolean equals(Object obj) {
        return obj instanceof CancellationTokenRegistration other
            && other.token == token
            && other.callback == callback
            && other.fired == fired;
    }

    @Override
    public int hashCode() {
        return System.identityHashCode(token) * 31 + System.identityHashCode(callback);
    }
}
