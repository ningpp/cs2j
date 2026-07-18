package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.MethodBuilder.
 */
public final class MethodBuilder {

    private final String _name;

    public MethodBuilder(String name) {
        this._name = name;
    }

    public String getName() {
        return _name;
    }

    public ILGenerator getILGenerator() {
        return new ILGenerator();
    }

    public MethodBuilder defineParameter(int position, ParameterAttributes attributes, String parameterName) {
        return this;
    }

    public MethodAttributes getAttributes() {
        return MethodAttributes.Public;
    }

    public Class<?> getReturnType() {
        return void.class;
    }

    public void setCustomAttribute(CustomAttributeBuilder customBuilder) {
    }
}
