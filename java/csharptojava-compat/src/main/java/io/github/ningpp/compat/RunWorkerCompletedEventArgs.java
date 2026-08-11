package io.github.ningpp.compat;

/**
 * System.ComponentModel.RunWorkerCompletedEventArgs compatibility class.
 */
public class RunWorkerCompletedEventArgs {

    private final Object result;
    private final Throwable error;
    private final boolean cancelled;

    public RunWorkerCompletedEventArgs() {
        this(null, null, false);
    }

    public RunWorkerCompletedEventArgs(Object result, Throwable error, boolean cancelled) {
        this.result = result;
        this.error = error;
        this.cancelled = cancelled;
    }

    public Object getResult() {
        return result;
    }

    public Object getError() {
        return error;
    }

    public boolean getCancelled() {
        return cancelled;
    }
}
