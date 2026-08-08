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

    public MethodInfo getDeclaredMethod(String name) {
        if (name == null || type == null) {
            return null;
        }
        for (java.lang.reflect.Method method : type.getDeclaredMethods()) {
            if (method.getName().equals(name)) {
                return new MethodInfo(method);
            }
        }
        return null;
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

    /** C# TypeInfo.GetDeclaredField(name). */
    public FieldInfo getDeclaredField(String name) {
        for (FieldInfo field : getDeclaredFields()) {
            if (field.getName().equals(name)) return field;
        }
        return null;
    }

    /** C# TypeInfo.GetDeclaredProperty(name). */
    public PropertyInfo getDeclaredProperty(String name) {
        for (PropertyInfo property : getDeclaredProperties()) {
            if (property.getName().equals(name)) return property;
        }
        return null;
    }

    /** C# CustomAttributes on the underlying type. */
    public CSharpGenericIterable<CustomAttributeData> getCustomAttributes() {
        CSharpList<CustomAttributeData> result = new CSharpList<>();
        java.lang.annotation.Annotation[] annotations = asClass().getAnnotations();
        for (java.lang.annotation.Annotation annotation : annotations) {
            if (annotation instanceof DebuggerDisplay display) {
                CSharpList<CustomAttributeTypedArgument> args = new CSharpList<>();
                args.add(new CustomAttributeTypedArgument(String.class, display.value()));
                result.add(new CustomAttributeData(DebuggerDisplayAttribute.class, args));
            } else if (annotation instanceof DebuggerTypeProxy proxy) {
                CSharpList<CustomAttributeTypedArgument> args = new CSharpList<>();
                args.add(new CustomAttributeTypedArgument(Class.class, proxy.value()));
                result.add(new CustomAttributeData(DebuggerTypeProxyAttribute.class, args));
            }
        }
        return CSharpGenericIterable.from(result);
    }

    /** C# Type.MakeGenericType(args) — stub returning the raw type. */
    public Class<?> makeGenericType(Class<?>... typeArguments) {
        return asClass();
    }
}
