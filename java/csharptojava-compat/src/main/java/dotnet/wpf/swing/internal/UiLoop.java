package dotnet.wpf.swing.internal;

import java.util.concurrent.CountDownLatch;

/**
 * Swing-backed UI loop for the WPF compat layer.
 *
 * <p>Owns the Swing event-dispatch thread lifecycle used by
 * {@code dotnet.wpf.swing.Application} and
 * {@code dotnet.wpf.swing.threading.Dispatcher}:
 * <ul>
 *   <li>{@link #invokeAndWait} runs a task on the EDT (starting it lazily),</li>
 *   <li>{@link #awaitShutdown} blocks the calling (main) thread until
 *       {@link #shutdown} is requested, keeping the EDT alive in between,</li>
 *   <li>{@link #isUiThread} answers "am I on the UI thread?".</li>
 * </ul>
 * </p>
 *
 * <p>In headless environments no EDT is started and {@link #invokeAndWait}
 * runs the task on the caller's thread.</p>
 */
public final class UiLoop {

    private static final UiLoop INSTANCE = new UiLoop();

    private final Object shutdownLock = new Object();
    private CountDownLatch shutdownLatch;
    private boolean shuttingDown;

    private UiLoop() {
    }

    public static UiLoop get() {
        return INSTANCE;
    }

    /** True when the JVM has no display available (tests, CI). */
    public static boolean isHeadless() {
        return java.awt.GraphicsEnvironment.isHeadless();
    }

    /** True when called from the Swing event-dispatch thread. */
    public boolean isUiThread() {
        return java.awt.EventQueue.isDispatchThread();
    }

    /**
     * Runs the task on the EDT, blocking the caller until it completes.
     * If already on the EDT the task runs directly. In headless mode the
     * task runs on the caller's thread. Unhandled exceptions are routed to
     * {@code Application.DispatcherUnhandledException} and rethrown.
     */
    public void invokeAndWait(Runnable task) {
        if (task == null) {
            return;
        }
        if (isHeadless() || isUiThread()) {
            try {
                task.run();
            } catch (Throwable t) {
                routeUnhandled(t);
                throw t;
            }
            return;
        }
        try {
            javax.swing.SwingUtilities.invokeAndWait(() -> {
                try {
                    task.run();
                } catch (Throwable t) {
                    routeUnhandled(t);
                    throw t;
                }
            });
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new RuntimeException("Interrupted while waiting for the UI thread", e);
        } catch (java.lang.reflect.InvocationTargetException e) {
            Throwable cause = e.getCause();
            if (cause instanceof RuntimeException) {
                throw (RuntimeException) cause;
            }
            if (cause instanceof Error) {
                throw (Error) cause;
            }
            throw new RuntimeException(cause);
        }
    }

    /**
     * Schedules the task on the EDT without blocking. In headless mode the
     * task is run on the caller's thread. Unhandled exceptions are routed to
     * {@code Application.DispatcherUnhandledException}.
     */
    public void invokeLater(Runnable task) {
        if (task == null) {
            return;
        }
        if (isHeadless() || isUiThread()) {
            try {
                task.run();
            } catch (Throwable t) {
                routeUnhandled(t);
            }
            return;
        }
        javax.swing.SwingUtilities.invokeLater(() -> {
            try {
                task.run();
            } catch (Throwable t) {
                routeUnhandled(t);
            }
        });
    }

    private void routeUnhandled(Throwable t) {
        try {
            dotnet.wpf.swing.Application app = dotnet.wpf.swing.Application.getCurrent();
            if (app != null) {
                app.raiseDispatcherUnhandledException(t);
            }
        } catch (Throwable ignored) {
            // routing must never mask the original exception
        }
    }

    /**
     * Blocks the calling thread until {@link #shutdown()} is requested.
     * Does nothing when running headless.
     */
    public void awaitShutdown() {
        if (isHeadless()) {
            return;
        }
        CountDownLatch latch;
        synchronized (shutdownLock) {
            if (shuttingDown) {
                return;
            }
            if (shutdownLatch == null) {
                shutdownLatch = new CountDownLatch(1);
            }
            latch = shutdownLatch;
        }
        try {
            latch.await();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    /**
     * Requests shutdown of the loop started by {@link #awaitShutdown()}.
     * Idempotent; safe to call from any thread, including the EDT.
     */
    public void shutdown() {
        synchronized (shutdownLock) {
            if (shuttingDown) {
                return;
            }
            shuttingDown = true;
            if (shutdownLatch != null) {
                shutdownLatch.countDown();
            }
        }
    }

    /** True after {@link #shutdown()} has been requested. */
    public boolean isShuttingDown() {
        synchronized (shutdownLock) {
            return shuttingDown;
        }
    }

    /**
     * Resets the loop so a new {@link #awaitShutdown()} cycle can start
     * (useful for embedding multiple app runs in one JVM).
     */
    public void reset() {
        synchronized (shutdownLock) {
            shuttingDown = false;
            shutdownLatch = null;
        }
    }
}