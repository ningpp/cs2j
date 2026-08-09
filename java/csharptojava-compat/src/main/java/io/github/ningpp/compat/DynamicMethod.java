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

    public DynamicMethod(String name, Class<?> returnType, Class<?>[] parameterTypes, Module owner, boolean restrictedSkipVisibility) {
        this._name = name;
    }

    public DynamicMethod(String name, Class<?> returnType, Class<?>[] parameterTypes, Object owner, boolean restrictedSkipVisibility) {
        this._name = name;
    }

    public DynamicMethod(String name, int methodAttributes, int callingConvention,
            Class<?> returnType, Class<?>[] parameterTypes, Object owner, boolean skipVisibility) {
        this._name = name;
    }

    public DynamicMethod(String name, int methodAttributes, int callingConvention,
            Class<?> returnType, Class<?>[] parameterTypes, boolean skipVisibility) {
        this._name = name;
    }

    public String getName() {
        return _name;
    }

    public ILGenerator getILGenerator() {
        return new ILGenerator();
    }

    @SuppressWarnings("unchecked")
    public <T> T createDelegate(Class<T> delegateType) {
        return null;
    }

    @SuppressWarnings("unchecked")
    public <T> T createDelegate(Class<T> delegateType, Object target) {
        return null;
    }

    public void setInitLocals(boolean value) {
    }
}
