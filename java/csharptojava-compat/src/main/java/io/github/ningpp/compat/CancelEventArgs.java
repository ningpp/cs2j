package io.github.ningpp.compat;

/**
 * System.ComponentModel.CancelEventArgs compatibility class.
 */
public class CancelEventArgs extends EventArgs {

    private boolean cancel;

    public CancelEventArgs() {
    }

    public CancelEventArgs(boolean cancel) {
        this.cancel = cancel;
    }

    public boolean getCancel() {
        return cancel;
    }

    public void setCancel(boolean value) {
        cancel = value;
    }
}