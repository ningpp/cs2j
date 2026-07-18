package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.OpCode.
 */
public final class OpCode {

    private final String _name;

    public OpCode(String name) {
        this._name = name;
    }

    public String getName() {
        return _name;
    }

    @Override
    public String toString() {
        return _name;
    }
}
