package io.github.ningpp.compat;

/**
 * Stub for System.Diagnostics.DebuggerBrowsableState.
 * Members are declared in .NET ordinal order so values()[] and fromValue() map correctly.
 */
public enum DebuggerBrowsableState {
    Never,
    Collapsed,
    RootHidden;

    public int getValue() {
        return ordinal();
    }

    public static DebuggerBrowsableState fromValue(int value) {
        DebuggerBrowsableState[] values = values();
        return (value >= 0 && value < values.length) ? values[value] : Never;
    }
}
