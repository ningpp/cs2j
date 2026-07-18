package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.Label.
 */
public final class Label {

    private static int _nextId = 0;
    private final int _id = _nextId++;

    public Label() {
    }

    @Override
    public String toString() {
        return "Label_" + _id;
    }
}
