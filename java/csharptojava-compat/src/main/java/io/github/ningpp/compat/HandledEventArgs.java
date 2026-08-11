package io.github.ningpp.compat;

/**
 * System.ComponentModel.HandledEventArgs compatibility class.
 */
public class HandledEventArgs {

    private boolean handled;

    public HandledEventArgs() {
    }

    public HandledEventArgs(boolean defaultHandledValue) {
        handled = defaultHandledValue;
    }

    public boolean getHandled() {
        return handled;
    }

    public void setHandled(boolean value) {
        handled = value;
    }
}
