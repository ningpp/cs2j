package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.PropertyBuilder.
 */
public final class PropertyBuilder {

    private final String _name;

    public PropertyBuilder(String name) {
        this._name = name;
    }

    public String getName() {
        return _name;
    }

    public void setGetMethod(MethodBuilder mdBuilder) {
    }
}
