package dotnet.wpf.swing.threading;

import dotnet.wpf.swing.internal.UiLoop;

/**
 * System.Windows.Threading.Dispatcher compatibility class.
 *
 * <p>Swing-backed: the UI thread is the Swing event-dispatch thread (EDT).
 * {@link #invoke(Runnable)} runs the callback directly when already on the
 * EDT and blocks the caller while executing it on the EDT otherwise;
 * {@link #beginInvoke}/{@link #invokeAsync} schedule callbacks without
 * blocking (priorities are preserved but not distinguished). {@link #run()}
 * blocks until the application requests shutdown.</p>
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
        return UiLoop.isHeadless();
    }

    public void verifyAccess() {
        if (UiLoop.isHeadless()) {
            return;
        }
        if (!checkAccess()) {
            throw new IllegalStateException(
                    "The calling thread cannot access this object because a different thread owns it.");
        }
    }

    /** True once the application has requested the UI loop to stop. */
    public boolean hasShutdownStarted() {
        return UiLoop.get().isShuttingDown();
    }

    /** True once the UI loop has stopped (equivalent to {@link #hasShutdownStarted()} in the compat layer). */
    public boolean hasShutdownFinished() {
        return UiLoop.get().isShuttingDown();
    }

    /** Requests shutdown of the UI loop (System.Windows.Threading.Dispatcher.Shutdown). */
    public void shutdown() {
        UiLoop.get().shutdown();
    }

    // ---- scheduling ----

    /** Schedules a callback on the UI thread without blocking (Normal priority). */
    public DispatcherOperation<Void> beginInvoke(Runnable callback) {
        return beginInvoke(DispatcherPriority.Normal, callback);
    }

    /** Schedules a callback on the UI thread with the given priority. */
    public DispatcherOperation<Void> beginInvoke(DispatcherPriority priority, Runnable callback) {
        DispatcherOperation<Void> op = DispatcherOperation.forRunnable(callback);
        schedule(op);
        return op;
    }

    /** Schedules a result-producing callback on the UI thread without blocking. */
    public <T> DispatcherOperation<T> invokeAsync(java.util.function.Supplier<T> callback) {
        return invokeAsync(DispatcherPriority.Normal, callback);
    }

    /** Schedules a result-producing callback on the UI thread with the given priority. */
    public <T> DispatcherOperation<T> invokeAsync(DispatcherPriority priority, java.util.function.Supplier<T> callback) {
        DispatcherOperation<T> op = DispatcherOperation.forSupplier(callback);
        schedule(op);
        return op;
    }

    private void schedule(DispatcherOperation<?> op) {
        if (op == null) {
            return;
        }
        if (UiLoop.isHeadless()) {
            // beginInvoke must never run on the caller's thread: keep a
            // dedicated single-thread executor so pending operations can
            // still be aborted before they execute
            EXECUTOR.execute(op::run);
        } else {
            UiLoop.get().invokeLater(op::run);
        }
    }

    private static final java.util.concurrent.ExecutorService EXECUTOR =
            java.util.concurrent.Executors.newSingleThreadExecutor(r -> {
                Thread t = new Thread(r, "csharptojava-compat-dispatcher");
                t.setDaemon(true);
                return t;
            });

    // ---- synchronous invocation ----

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

    // ---- nested frames ----

    /**
     * Runs a nested dispatcher frame: blocks the calling thread until
     * {@link DispatcherFrame#setContinue(boolean) continue=false} (or the
     * frame is exited via {@link #exitAllFrames}). The Swing event dispatch
     * thread keeps running independently while the caller is blocked.
     */
    public void pushFrame() {
        pushFrame(new DispatcherFrame());
    }

    public void pushFrame(DispatcherFrame frame) {
        if (frame == null) {
            return;
        }
        while (frame.getContinue() && !exitAllFramesRequested) {
            try {
                Thread.sleep(10);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
        }
    }

    /** Requests that all nested frames exit at their next check. */
    public void exitAllFrames() {
        exitAllFramesRequested = true;
    }

    private volatile boolean exitAllFramesRequested;

    /**
     * Blocks the calling thread and pumps the UI event loop until the
     * application requests shutdown.
     */
    public static void run() {
        UiLoop.get().awaitShutdown();
    }
}