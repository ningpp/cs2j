package io.github.ningpp.compat;

/**
 * Stub for System.Diagnostics.DebuggerBrowsableAttribute.
 */
public class DebuggerBrowsableAttribute {
    private final DebuggerBrowsableState state;

    public DebuggerBrowsableAttribute(DebuggerBrowsableState state) {
        this.state = state;
    }

    public DebuggerBrowsableState getState() {
        return state;
    }
}
