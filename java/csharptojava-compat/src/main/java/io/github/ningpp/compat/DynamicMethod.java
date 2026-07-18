package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.DynamicMethod.
 */
public final class DynamicMethod {

    private final String _name;

    public DynamicMethod(String name, Class<?> returnType, Class<?>[] parameterTypes) {
        this._name = name;
    }

    public DynamicMethod(String name, Class<?> returnType, Class<?>[] parameterTypes, Class<?> owner) {
        this._name = name;
    }

    public DynamicMethod(String name, Class<?> returnType, Class<?>[] parameterTypes, boolean restrictedSkipVisibility) {
        this._name = name;
    }

    public String getName() {
        return _name;
    }

    public ILGenerator getILGenerator() {
        return new ILGenerator();
    }

    public Object createDelegate(Class<?> delegateType) {
        return null;
    }

    public Object createDelegate(Class<?> delegateType, Object target) {
        return null;
    }

    public void setInitLocals(boolean value) {
    }
}
