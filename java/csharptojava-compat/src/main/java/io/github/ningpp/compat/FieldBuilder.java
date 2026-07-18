package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.FieldBuilder.
 */
public final class FieldBuilder {

    private final String _name;
    private final Class<?> _type;

    public FieldBuilder(String name, Class<?> type) {
        this._name = name;
        this._type = type;
    }

    public String getName() {
        return _name;
    }

    public Class<?> getFieldType() {
        return _type;
    }
}
