package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.AssemblyBuilder.
 */
public final class AssemblyBuilder {

    private final String _name;

    public AssemblyBuilder(String name) {
        this._name = name;
    }

    public String getName() {
        return _name;
    }

    public ModuleBuilder defineDynamicModule(String name) {
        return new ModuleBuilder(name);
    }

    public void setCustomAttribute(CustomAttributeBuilder customBuilder) {
    }

    public static AssemblyBuilder defineDynamicAssembly(Object assemblyName, AssemblyBuilderAccess access) {
        return new AssemblyBuilder(String.valueOf(assemblyName));
    }
}
