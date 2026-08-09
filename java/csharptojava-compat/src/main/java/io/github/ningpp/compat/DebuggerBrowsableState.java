package io.github.ningpp.compat;

/**
 * Stub for System.Diagnostics.DebuggerBrowsableState.
 * Values mirror .NET 10: Never=0, Collapsed=2, RootHidden=3 (1 is reserved/unused).
 */
public enum DebuggerBrowsableState {
    Never(0),
    Collapsed(2),
    RootHidden(3);

    private final int value;

    DebuggerBrowsableState(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static DebuggerBrowsableState fromValue(int value) {
        for (DebuggerBrowsableState state : values()) {
            if (state.value == value) {
                return state;
            }
        }
        return Never;
    }
}
