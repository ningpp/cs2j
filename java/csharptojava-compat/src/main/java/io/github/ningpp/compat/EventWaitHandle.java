package io.github.ningpp.compat;

/**
 * System.Threading.EventWaitHandle compatibility class (Swing-backed).
 */
public class EventWaitHandle implements AutoCloseable {

    private final boolean autoReset;
    private boolean signaled;

    public EventWaitHandle(boolean initialState, EventResetMode mode) {
        this.signaled = initialState;
        this.autoReset = mode == null || mode == EventResetMode.AutoReset;
    }

    public boolean set() {
        synchronized (this) {
            signaled = true;
            notifyAll();
        }
        return true;
    }

    public boolean reset() {
        synchronized (this) {
            signaled = false;
        }
        return true;
    }

    public boolean waitOne() {
        synchronized (this) {
            while (!signaled) {
                try {
                    wait();
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    return false;
                }
            }
            if (autoReset) {
                signaled = false;
            }
            return true;
        }
    }

    @Override
    public void close() {
        // no-op in the Swing-backed compat layer
    }
}
