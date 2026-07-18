package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.ModuleBuilder.
 */
public final class ModuleBuilder {

    private final String _name;

    public ModuleBuilder(String name) {
        this._name = name;
    }

    public String getName() {
        return _name;
    }

    public TypeBuilder defineType(String name, TypeAttributes attributes) {
        return new TypeBuilder(name);
    }

    public TypeBuilder defineType(String name, TypeAttributes attributes, Class<?> parent) {
        return new TypeBuilder(name);
    }
}
