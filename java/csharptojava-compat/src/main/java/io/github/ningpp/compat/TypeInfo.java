package io.github.ningpp.compat;

import java.lang.reflect.Modifier;

public final class TypeInfo {
    private final Class<?> type;

    private TypeInfo(Class<?> type) {
        this.type = type;
    }

    public static TypeInfo of(Class<?> type) {
        return new TypeInfo(type);
    }

    public Class<?> asClass() {
        return type;
    }

    public Class<?> getBaseType() {
        return type.getSuperclass();
    }

    public boolean getIsValueType() {
        return type.isPrimitive()
            || Number.class.isAssignableFrom(type)
            || type == Boolean.class
            || type == Character.class
            || type.isEnum();
    }

    public boolean getIsGenericType() {
        return type.getTypeParameters().length > 0;
    }

    public boolean getIsGenericTypeDefinition() {
        return getIsGenericType();
    }

    public boolean isInterface() {
        return type.isInterface();
    }

    public boolean isEnum() {
        return type.isEnum();
    }

    public boolean isAssignableFrom(TypeInfo source) {
        return source != null && type.isAssignableFrom(source.type);
    }

    public boolean isAssignableFrom(Class<?> source) {
        return source != null && type.isAssignableFrom(source);
    }

    public Class<?>[] getGenericTypeArguments() {
        return new Class<?>[0];
    }

    public Class<?>[] getImplementedInterfaces() {
        return type.getInterfaces();
    }

    public boolean isSubclassOf(Class<?> baseType) {
        return baseType != null && baseType.isAssignableFrom(type) && type != baseType;
    }

    public boolean getIsAbstract() {
        return Modifier.isAbstract(type.getModifiers());
    }
}
