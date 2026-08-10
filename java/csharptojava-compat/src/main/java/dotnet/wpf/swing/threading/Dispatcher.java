package dotnet.wpf.swing.threading;

/**
 * System.Windows.Threading.Dispatcher compatibility class.
 * Swing-backed: Invoke/RunLater merely run the callback (on the caller's thread in a test
 * environment, or queued on the EDT in a headful one).
 */
public final class Dispatcher {

    private static final Dispatcher CURRENT = new Dispatcher();
    private static final Thread MAIN_THREAD = Thread.currentThread();

    public static Dispatcher getCurrentDispatcher() {
        return CURRENT;
    }

    public static Dispatcher fromThread(Thread thread) {
        return CURRENT;
    }

    public Thread getThread() {
        return MAIN_THREAD;
    }

    public boolean checkAccess() {
        return Thread.currentThread() == MAIN_THREAD
                || (java.awt.EventQueue.isDispatchThread() && MAIN_THREAD == Thread.currentThread());
    }

    public void verifyAccess() {
        // no-op for compatibility
    }

    public void invoke(Runnable callback) {
        if (callback == null) return;
        if (checkAccess()) {
            callback.run();
        } else {
            try {
                javax.swing.SwingUtilities.invokeAndWait(callback);
            } catch (Exception e) {
                callback.run();
            }
        }
    }

    public <T> T invoke(java.util.function.Supplier<T> callback) {
        if (callback == null) return null;
        if (checkAccess()) {
            return callback.get();
        }
        final Object lock = new Object();
        final Object[] box = new Object[1];
        final Throwable[] err = new Throwable[1];
        javax.swing.SwingUtilities.invokeLater(() -> {
            try {
                box[0] = callback.get();
            } catch (Throwable t) {
                err[0] = t;
            } finally {
                synchronized (lock) {
                    lock.notifyAll();
                }
            }
        });
        synchronized (lock) {
            try {
                lock.wait();
            } catch (InterruptedException ignored) {
            }
        }
        if (err[0] != null) {
            throw new RuntimeException(err[0]);
        }
        //noinspection unchecked
        return (T) box[0];
    }

    public static void run() {
        // no-op: no message loop needed for Swing-backed testing.
    }
}
