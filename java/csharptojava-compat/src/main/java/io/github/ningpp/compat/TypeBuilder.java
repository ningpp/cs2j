package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.TypeBuilder.
 */
public final class TypeBuilder {

    private final String _name;

    public TypeBuilder(String name) {
        this._name = name;
    }

    public String getName() {
        return _name;
    }

    public FieldBuilder defineField(String fieldName, Class<?> type, FieldAttributes attributes) {
        return new FieldBuilder(fieldName, type);
    }

    public MethodBuilder defineMethod(String name, MethodAttributes attributes, Class<?> returnType, Class<?>[] parameterTypes) {
        return new MethodBuilder(name);
    }

    public MethodBuilder defineMethod(String name, MethodAttributes attributes) {
        return new MethodBuilder(name);
    }

    public ConstructorBuilder defineDefaultConstructor(MethodAttributes attributes) {
        return new ConstructorBuilder();
    }

    public ConstructorBuilder defineTypeInitializer() {
        return new ConstructorBuilder();
    }

    public PropertyBuilder defineProperty(String name, PropertyAttributes attributes, Class<?> returnType, Class<?>[] parameterTypes) {
        return new PropertyBuilder(name);
    }

    public TypeInfo createTypeInfo() {
        return TypeInfo.of(Object.class);
    }

    public void defineInitializedData(String name, byte[] data, FieldAttributes attributes) {
    }
}
