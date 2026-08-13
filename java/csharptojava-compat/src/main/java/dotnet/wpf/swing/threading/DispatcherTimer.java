package dotnet.wpf.swing.threading;

import io.github.ningpp.compat.EventArgs;

/**
 * System.Windows.Threading.DispatcherTimer compatibility class.
 * Backed by a {@link javax.swing.Timer} so Tick callbacks run on the
 * Swing event-dispatch thread (safe for UI updates).
 */
public final class DispatcherTimer {

    private boolean isEnabled;
    private Object tag;
    private long intervalMilliseconds = 100;
    private javax.swing.Timer timer;
    private final java.util.List<EventHandlerLike> tickListeners = new java.util.ArrayList<>();
    private final Object sync = new Object();

    @FunctionalInterface
    public interface EventHandlerLike {
        void handle(Object sender, EventArgs e);
    }

    public Dispatcher getDispatcher() {
        return Dispatcher.getCurrentDispatcher();
    }

    public long getIntervalMilliseconds() {
        return intervalMilliseconds;
    }

    public void setIntervalMilliseconds(long value) {
        intervalMilliseconds = value;
        synchronized (sync) {
            if (timer != null) {
                timer.setDelay((int) Math.max(1, intervalMilliseconds));
            }
        }
    }

    public void setInterval(io.github.ningpp.compat.CSharpTimeSpan value) {
        if (value == null) {
            intervalMilliseconds = 0;
            return;
        }
        intervalMilliseconds = (long) value.getTotalMilliseconds();
    }

    public boolean getIsEnabled() {
        return isEnabled;
    }

    public void setIsEnabled(boolean value) {
        if (value) {
            start();
        } else {
            stop();
        }
    }

    public Object getTag() {
        return tag;
    }

    public void setTag(Object value) {
        tag = value;
    }

    public void addTickListener(EventHandlerLike handler) {
        synchronized (sync) {
            tickListeners.add(handler);
        }
    }

    public void removeTickListener(EventHandlerLike handler) {
        synchronized (sync) {
            tickListeners.remove(handler);
        }
    }

    public void start() {
        synchronized (sync) {
            if (timer == null) {
                timer = new javax.swing.Timer((int) Math.max(1, intervalMilliseconds),
                        e -> fireTick());
            }
            timer.setDelay((int) Math.max(1, intervalMilliseconds));
            timer.start();
            isEnabled = true;
        }
    }

    public void stop() {
        synchronized (sync) {
            if (timer != null) {
                timer.stop();
            }
            isEnabled = false;
        }
    }

    private void fireTick() {
        synchronized (sync) {
            if (!isEnabled) {
                return; // an event may still be in flight after stop()
            }
        }
        java.util.List<EventHandlerLike> copy;
        synchronized (sync) {
            copy = new java.util.ArrayList<>(tickListeners);
        }
        for (EventHandlerLike handler : copy) {
            try {
                handler.handle(this, null);
            } catch (Throwable ignored) {
                // tick callbacks must not kill the timer thread
            }
        }
    }
}