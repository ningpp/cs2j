package dotnet.wpf.swing.threading;

import dotnet.wpf.swing.internal.UiLoop;

/**
 * System.Windows.Threading.Dispatcher compatibility class.
 *
 * <p>Swing-backed: the UI thread is the Swing event-dispatch thread (EDT).
 * {@link #invoke(Runnable)} runs the callback directly when already on the
 * EDT and blocks the caller while executing it on the EDT otherwise.
 * {@link #run()} blocks until the application requests shutdown.</p>
 */
public final class Dispatcher {

    private static final Dispatcher CURRENT = new Dispatcher();

    public static Dispatcher getCurrentDispatcher() {
        return CURRENT;
    }

    public static Dispatcher fromThread(Thread thread) {
        return CURRENT;
    }

    private static volatile Thread uiThread;

    public Thread getThread() {
        if (uiThread == null && UiLoop.get().isUiThread()) {
            uiThread = Thread.currentThread();
        }
        return uiThread == null ? Thread.currentThread() : uiThread;
    }

    public boolean checkAccess() {
        if (UiLoop.get().isUiThread()) {
            uiThread = Thread.currentThread();
            return true;
        }
        return false;
    }

    public void verifyAccess() {
        // no-op for compatibility
    }

    public void invoke(Runnable callback) {
        if (callback == null) {
            return;
        }
        UiLoop.get().invokeAndWait(callback);
    }

    public <T> T invoke(java.util.function.Supplier<T> callback) {
        if (callback == null) {
            return null;
        }
        if (UiLoop.get().isUiThread()) {
            return callback.get();
        }
        final Object[] box = new Object[1];
        final Throwable[] err = new Throwable[1];
        UiLoop.get().invokeAndWait(() -> {
            try {
                box[0] = callback.get();
            } catch (Throwable t) {
                err[0] = t;
            }
        });
        if (err[0] != null) {
            throw new RuntimeException(err[0]);
        }
        //noinspection unchecked
        return (T) box[0];
    }

    /**
     * Blocks the calling thread and pumps the UI event loop until the
     * application requests shutdown.
     */
    public static void run() {
        UiLoop.get().awaitShutdown();
    }
}