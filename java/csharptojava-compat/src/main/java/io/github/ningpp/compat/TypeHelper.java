package io.github.ningpp.compat;

import java.lang.reflect.Array;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayList;
import java.util.List;

/**
 * Bridges C# System.Type reflection idioms to java.lang.Class.
 */
public final class TypeHelper {
    private TypeHelper() {
    }

    /**
     * Mirrors C# Type.GetTypeCode() for common primitive/object mappings.
     */
    public static TypeCode getTypeCode(Class<?> type) {
        if (type == null) {
            return TypeCode.Empty;
        }
        if (type == boolean.class || type == Boolean.class) {
            return TypeCode.Boolean;
        }
        if (type == char.class || type == Character.class) {
            return TypeCode.Char;
        }
        if (type == byte.class || type == Byte.class) {
            return TypeCode.Byte;
        }
        if (type == short.class || type == Short.class) {
            return TypeCode.Int16;
        }
        if (type == int.class || type == Integer.class) {
            return TypeCode.Int32;
        }
        if (type == long.class || type == Long.class) {
            return TypeCode.Int64;
        }
        if (type == float.class || type == Float.class) {
            return TypeCode.Single;
        }
        if (type == double.class || type == Double.class) {
            return TypeCode.Double;
        }
        if (type == String.class) {
            return TypeCode.String;
        }
        return TypeCode.Object;
    }

    public static Class<?> getBaseType(Class<?> type) {
        return type == null ? null : type.getSuperclass();
    }

    public static Class<?> getDeclaringType(Class<?> type) {
        return type == null ? null : type.getDeclaringClass();
    }

    public static boolean getIsGenericType(Class<?> type) {
        return type != null && type.getTypeParameters().length > 0;
    }

    public static boolean getContainsGenericParameters(Class<?> type) {
        return getIsGenericType(type);
    }

    public static java.lang.reflect.TypeVariable<?>[] getGenericArguments(Class<?> type) {
        return type == null ? new java.lang.reflect.TypeVariable<?>[0] : type.getTypeParameters();
    }

    public static Class<?> getGenericTypeDefinition(Class<?> type) {
        if (type == null) {
            return null;
        }
        Class<?> raw = type;
        while (raw.getEnclosingClass() != null && raw.getTypeParameters().length == 0) {
            raw = raw.getEnclosingClass();
        }
        return raw;
    }

    public static boolean getIsAbstract(Class<?> type) {
        return type != null && Modifier.isAbstract(type.getModifiers()) && !type.isInterface();
    }

    public static boolean getIsValueType(Class<?> type) {
        return type != null && type.isPrimitive();
    }

    public static boolean getIsVisible(Class<?> type) {
        return type != null && Modifier.isPublic(type.getModifiers());
    }

    public static boolean getIsNestedPublic(Class<?> type) {
        return type != null && type.isMemberClass() && Modifier.isPublic(type.getModifiers());
    }

    public static boolean getIsClass(Class<?> type) {
        return type != null && !type.isInterface() && !type.isPrimitive() && !type.isArray();
    }

    public static int getArrayRank(Class<?> type) {
        if (type == null || !type.isArray()) {
            return 0;
        }
        int rank = 0;
        Class<?> current = type;
        while (current.isArray()) {
            rank++;
            current = current.getComponentType();
        }
        return rank;
    }

    public static MethodInfo[] getMethods(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new MethodInfo[0];
        }
        Method[] methods = type.getMethods();
        List<MethodInfo> result = new ArrayList<>();
        for (Method method : methods) {
            if (matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                result.add(new MethodInfo(method));
            }
        }
        return result.toArray(new MethodInfo[0]);
    }

    public static MethodInfo[] getMethods(Class<?> type) {
        return getMethods(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static FieldInfo[] getFields(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new FieldInfo[0];
        }
        Field[] fields = type.getFields();
        List<FieldInfo> result = new ArrayList<>();
        for (Field field : fields) {
            if (matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                result.add(new FieldInfo(field));
            }
        }
        return result.toArray(new FieldInfo[0]);
    }

    public static FieldInfo[] getFields(Class<?> type) {
        return getFields(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static ConstructorInfo[] getConstructors(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new ConstructorInfo[0];
        }
        Constructor<?>[] ctors = type.getConstructors();
        List<ConstructorInfo> result = new ArrayList<>();
        for (Constructor<?> ctor : ctors) {
            if (matchesBindingFlags(ctor.getModifiers(), bindingFlags)) {
                result.add(new ConstructorInfo(ctor));
            }
        }
        return result.toArray(new ConstructorInfo[0]);
    }

    public static ConstructorInfo[] getConstructors(Class<?> type) {
        return getConstructors(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static ConstructorInfo getConstructorInfo(Class<?> type, int bindingFlags, Class<?>... parameterTypes) {
        if (type == null) {
            return null;
        }
        try {
            return new ConstructorInfo(type.getConstructor(parameterTypes));
        } catch (NoSuchMethodException e) {
            return null;
        }
    }

    public static ConstructorInfo getConstructor(Class<?> type, int bindingFlags, Class<?>... parameterTypes) {
        if (type == null) {
            return null;
        }
        for (Constructor<?> ctor : type.getDeclaredConstructors()) {
            if (matchesParameterTypes(ctor.getParameterTypes(), parameterTypes)
                    && matchesBindingFlags(ctor.getModifiers(), bindingFlags)) {
                return new ConstructorInfo(ctor);
            }
        }
        return null;
    }

    public static MemberInfo getMember(Class<?> type, String name) {
        return getMember(type, name, 0);
    }

    public static MemberInfo getMember(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null) {
            return null;
        }
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name) && matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                return new MethodInfo(method);
            }
        }
        for (Field field : type.getFields()) {
            if (field.getName().equals(name) && matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                return new FieldInfo(field);
            }
        }
        return null;
    }

    public static MemberInfo[] getMembers(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new MemberInfo[0];
        }
        List<MemberInfo> members = new ArrayList<>();
        for (Method method : type.getMethods()) {
            if (matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                members.add(new MethodInfo(method));
            }
        }
        for (Field field : type.getFields()) {
            if (matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                members.add(new FieldInfo(field));
            }
        }
        return members.toArray(new MemberInfo[0]);
    }

    public static MemberInfo[] getMembers(Class<?> type) {
        return getMembers(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static MemberInfo[] getMembers(Class<?> type, String name) {
        return getMembers(type, name, 0);
    }

    public static MemberInfo[] getMembers(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null) {
            return new MemberInfo[0];
        }
        List<MemberInfo> members = new ArrayList<>();
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name) && matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                members.add(new MethodInfo(method));
            }
        }
        for (Field field : type.getFields()) {
            if (field.getName().equals(name) && matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                members.add(new FieldInfo(field));
            }
        }
        return members.toArray(new MemberInfo[0]);
    }

    public static FieldInfo getField(Class<?> type, String name) {
        if (type == null || name == null) {
            return null;
        }
        try {
            return new FieldInfo(type.getField(name));
        } catch (NoSuchFieldException e) {
            return null;
        }
    }

    /**
     * Bridges C# Type.GetProperty(name) semantics.
     * Looks up a Java bean property by its C#/Java property name (e.g. "Value" or "value").
     */
    public static PropertyInfo getProperty(Class<?> type, String name) {
        if (type == null || name == null || name.isEmpty()) {
            return null;
        }
        String capitalized = Character.toUpperCase(name.charAt(0)) + name.substring(1);
        String getterName = "get" + capitalized;
        String isGetterName = "is" + capitalized;
        String setterName = "set" + capitalized;

        Method getter = null;
        Method setter = null;
        for (Method method : type.getMethods()) {
            if (method.getParameterCount() != 0 || method.getReturnType() == void.class) {
                continue;
            }
            String methodName = method.getName();
            if (getter == null && (methodName.equals(getterName) || methodName.equals(isGetterName))) {
                getter = method;
            }
            if (setter == null && methodName.equals(setterName) && method.getParameterCount() == 1) {
                setter = method;
            }
            if (getter != null && setter != null) {
                break;
            }
        }
        if (getter == null && setter == null) {
            return null;
        }
        return new PropertyInfo(getter, setter);
    }

    public static String getFullName(Class<?> type) {
        return type == null ? null : type.getName();
    }

    public static Class<?> getElementType(Class<?> type) {
        return type == null ? null : type.getComponentType();
    }

    public static boolean getIsGenericParameter(Class<?> type) {
        return type != null && java.lang.reflect.TypeVariable.class.isInstance(type);
    }

    public static Class<?> makeArrayType(Class<?> elementType) {
        if (elementType == null) {
            return null;
        }
        return java.lang.reflect.Array.newInstance(elementType, 0).getClass();
    }

    public static Object newArrayInstance(Class<?> elementType, int length) {
        if (elementType == null) {
            return null;
        }
        return java.lang.reflect.Array.newInstance(elementType, length);
    }

    public static MethodInfo getMethod(Class<?> type, String name, Class<?>... parameterTypes) {
        if (type == null || name == null) {
            return null;
        }
        try {
            return new MethodInfo(type.getMethod(name, parameterTypes));
        } catch (NoSuchMethodException e) {
            return null;
        }
    }

    /**
     * Bridges C# {@code Type.GetMethod(name, BindingFlags)} semantics.
     */
    public static MethodInfo getMethod(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null) {
            return null;
        }
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name) && matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                return new MethodInfo(method);
            }
        }
        for (Method method : type.getDeclaredMethods()) {
            if (method.getName().equals(name) && matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                return new MethodInfo(method);
            }
        }
        return null;
    }

    public static Object[] getCustomAttributes(Class<?> type, boolean inherit) {
        if (type == null) {
            return new Object[0];
        }
        return type.getAnnotations();
    }

    public static Object[] getCustomAttributes(Class<?> type, Class<?> attributeType, boolean inherit) {
        if (type == null || attributeType == null) {
            return new Object[0];
        }
        return type.getAnnotationsByType((Class) attributeType);
    }

    /**
     * Mirrors C# Attribute.IsDefined(element, attributeType, inherit) for Class targets.
     */
    public static boolean isDefined(Class<?> type, Class<?> attributeType, boolean inherit) {
        if (type == null || attributeType == null) {
            return false;
        }
        return type.isAnnotationPresent((Class) attributeType);
    }

    public static MemberInfo[] getDefaultMembers(Class<?> type) {
        if (type == null) {
            return new MemberInfo[0];
        }
        List<MemberInfo> members = new ArrayList<>();
        DefaultMemberAttribute attr = type.getAnnotation(DefaultMemberAttribute.class);
        if (attr == null) {
            return members.toArray(new MemberInfo[0]);
        }
        String name = attr.value();
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name)) {
                members.add(new MethodInfo(method));
            }
        }
        for (Field field : type.getFields()) {
            if (field.getName().equals(name)) {
                members.add(new FieldInfo(field));
            }
        }
        return members.toArray(new MemberInfo[0]);
    }

    @SuppressWarnings("unchecked")
    private static <T> T[] filterByBindingFlags(T[] members, int bindingFlags, Class<T> clazz) {
        if (bindingFlags == 0) {
            return members;
        }
        List<T> result = new ArrayList<>();
        for (T member : members) {
            int mods;
            if (member instanceof Method m) {
                mods = m.getModifiers();
            } else if (member instanceof Field f) {
                mods = f.getModifiers();
            } else if (member instanceof Constructor<?> c) {
                mods = c.getModifiers();
            } else {
                continue;
            }
            if (matchesBindingFlags(mods, bindingFlags)) {
                result.add(member);
            }
        }
        return result.toArray((T[]) Array.newInstance(clazz, 0));
    }

    private static boolean matchesBindingFlags(int modifiers, int bindingFlags) {
        // C# BindingFlags: Public=0x10, NonPublic=0x20, Static=0x8, Instance=0x4
        boolean wantPublic = (bindingFlags & 0x10) != 0;
        boolean wantNonPublic = (bindingFlags & 0x20) != 0;
        boolean wantStatic = (bindingFlags & 0x08) != 0;
        boolean wantInstance = (bindingFlags & 0x04) != 0;

        boolean isPublic = Modifier.isPublic(modifiers);
        boolean isStatic = Modifier.isStatic(modifiers);

        if (wantPublic && !isPublic) {
            return false;
        }
        if (wantNonPublic && isPublic) {
            return false;
        }
        if (wantStatic && !isStatic) {
            return false;
        }
        if (wantInstance && isStatic) {
            return false;
        }
        return true;
    }

    private static boolean matchesParameterTypes(Class<?>[] actual, Class<?>... expected) {
        if (actual.length != expected.length) {
            return false;
        }
        for (int i = 0; i < actual.length; i++) {
            if (!actual[i].equals(expected[i])) {
                return false;
            }
        }
        return true;
    }
}
