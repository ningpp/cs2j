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

    public PropertyInfo[] getDeclaredProperties() {
        return PropertyInfo.getProperties(type);
    }

    public FieldInfo[] getDeclaredFields() {
        java.lang.reflect.Field[] fields = type.getDeclaredFields();
        FieldInfo[] result = new FieldInfo[fields.length];
        for (int i = 0; i < fields.length; i++) {
            result[i] = new FieldInfo(fields[i]);
        }
        return result;
    }

    public ConstructorInfo[] getDeclaredConstructors() {
        java.lang.reflect.Constructor<?>[] ctors = type.getDeclaredConstructors();
        ConstructorInfo[] result = new ConstructorInfo[ctors.length];
        for (int i = 0; i < ctors.length; i++) {
            result[i] = new ConstructorInfo(ctors[i]);
        }
        return result;
    }

    public MethodInfo[] getDeclaredMethods() {
        java.lang.reflect.Method[] methods = type.getDeclaredMethods();
        MethodInfo[] result = new MethodInfo[methods.length];
        for (int i = 0; i < methods.length; i++) {
            result[i] = new MethodInfo(methods[i]);
        }
        return result;
    }

    public MemberInfo[] getDeclaredMembers() {
        java.lang.reflect.Field[] fields = type.getDeclaredFields();
        java.lang.reflect.Method[] methods = type.getDeclaredMethods();
        MemberInfo[] result = new MemberInfo[fields.length + methods.length];
        int i = 0;
        for (java.lang.reflect.Field f : fields) {
            result[i++] = new FieldInfo(f);
        }
        for (java.lang.reflect.Method m : methods) {
            result[i++] = new MethodInfo(m);
        }
        return result;
    }
}
