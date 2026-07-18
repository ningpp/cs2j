package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.CallingConventions.
 */
public enum CallingConventions {
    Standard(1),
    VarArgs(2),
    Any(3),
    HasThis(32),
    ExplicitThis(64);

    private final int _value;

    CallingConventions(int value) {
        this._value = value;
    }

    public int getValue() {
        return _value;
    }
}
