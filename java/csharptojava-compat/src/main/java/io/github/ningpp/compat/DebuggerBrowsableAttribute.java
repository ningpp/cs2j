package io.github.ningpp.compat;

/** Minimal System.Diagnostics.DebuggerBrowsableAttribute compat holder. */
public final class DebuggerBrowsableAttribute {
    private final DebuggerBrowsableState state;

    public DebuggerBrowsableAttribute(DebuggerBrowsableState state) {
        this.state = state;
    }

    public DebuggerBrowsableAttribute(int state) {
        this.state = DebuggerBrowsableState.fromValue(state);
    }

    public DebuggerBrowsableState getState() {
        return state;
    }
}
