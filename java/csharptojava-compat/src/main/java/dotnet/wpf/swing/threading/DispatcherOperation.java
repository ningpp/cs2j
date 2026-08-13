package dotnet.wpf.swing.threading;

/**
 * System.Windows.Threading.DispatcherOperation compatibility class.
 * Wraps a callback scheduled via {@link Dispatcher#beginInvoke} /
 * {@link Dispatcher#invokeAsync} and tracks its execution status.
 *
 * @param <T> result type of the wrapped callback ({@code Void} for runnables)
 */
public class DispatcherOperation<T> {

    public enum Status {
        Pending,
        Completed,
        Aborted
    }

    private final Runnable callback;
    private final java.util.function.Supplier<T> supplier;
    private volatile Status status = Status.Pending;
    private volatile T result;
    private volatile Throwable error;

    private DispatcherOperation(Runnable callback) {
        this.callback = callback;
        this.supplier = null;
    }

    private DispatcherOperation(java.util.function.Supplier<T> supplier) {
        this.callback = null;
        this.supplier = supplier;
    }

    /** Creates an operation wrapping a plain runnable. */
    static DispatcherOperation<Void> forRunnable(Runnable callback) {
        return new DispatcherOperation<>(callback);
    }

    /** Creates an operation wrapping a result-producing callback. */
    static <T> DispatcherOperation<T> forSupplier(java.util.function.Supplier<T> supplier) {
        return new DispatcherOperation<>(supplier);
    }

    public Status getStatus() {
        return status;
    }

    /** The callback's result, or null while pending / for runnables. */
    public T getResult() {
        return result;
    }

    /** Waits until the operation has run. */
    public void waitForCompletion() {
        // the compat layer schedules via the UI loop; invokeLater-style
        // operations run on the EDT, so block until the status changes
        while (status == Status.Pending) {
            try {
                Thread.sleep(5);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
        }
    }

    /** Aborts a pending operation (no-op once running or completed). */
    public void abort() {
        if (status == Status.Pending) {
            status = Status.Aborted;
        }
    }

    /** The exception thrown by the callback, if any. */
    public Throwable getError() {
        return error;
    }

    void run() {
        if (status != Status.Pending) {
            return;
        }
        try {
            if (supplier != null) {
                result = supplier.get();
            } else if (callback != null) {
                callback.run();
            }
            status = Status.Completed;
        } catch (Throwable t) {
            error = t;
            status = Status.Aborted;
            throw t;
        }
    }
}