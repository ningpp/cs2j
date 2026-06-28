package io.github.ningpp.compat;

import java.lang.reflect.Field;
import java.lang.reflect.Method;

/**
 * Helper for System.Type reflection methods that don't map directly to java.lang.Class.
 */
public class TypeHelper {

    /** Mirrors C# Type.GetProperty(name) — returns PropertyInfo or null if not found */
    public static PropertyInfo getProperty(Class<?> clazz, String propertyName) {
        String getterName = "get" + propertyName;
        String isGetterName = "is" + propertyName;
        for (Method m : clazz.getMethods()) {
            if (m.getParameterCount() == 0 &&
                (m.getName().equals(getterName) || m.getName().equals(isGetterName))) {
                return new PropertyInfo(m);
            }
        }
        return null;
    }

    /** Mirrors C# Type.GetField(name) — returns Field or null */
    public static Field getField(Class<?> clazz, String name) {
        try {
            return clazz.getDeclaredField(name);
        } catch (NoSuchFieldException e) {
            return null;
        }
    }

    /** Mirrors C# Type.EmptyTypes */
    public static Class<?>[] emptyTypes() {
        return new Class<?>[0];
    }

    /** Mirrors C# Type.GetTypeCode(Type) for common Java runtime types. */
    public static TypeCode getTypeCode(Class<?> type) {
        if (type == null) return TypeCode.Empty;
        if (type == boolean.class || type == Boolean.class) return TypeCode.Boolean;
        if (type == char.class || type == Character.class) return TypeCode.Char;
        if (type == byte.class || type == Byte.class || type == CSharpByte.class) return TypeCode.Byte;
        if (type == short.class || type == Short.class) return TypeCode.Int16;
        if (type == int.class || type == Integer.class) return TypeCode.Int32;
        if (type == long.class || type == Long.class) return TypeCode.Int64;
        if (type == float.class || type == Float.class) return TypeCode.Single;
        if (type == double.class || type == Double.class) return TypeCode.Double;
        if (type == String.class) return TypeCode.String;
        if (type == Decimal.class) return TypeCode.Decimal;
        if (type == CSharpDateTime.class) return TypeCode.DateTime;
        if (type == CSharpSByte.class) return TypeCode.SByte;
        if (type == CSharpUInt16.class) return TypeCode.UInt16;
        if (type == CSharpUInt32.class) return TypeCode.UInt32;
        if (type == CSharpUInt64.class) return TypeCode.UInt64;
        return TypeCode.Object;
    }

    /** Mirrors C# Type.GetMethod(name, Type[]) — returns Method or null */
    public static java.lang.reflect.Method getMethod(Class<?> clazz, String name, Class<?>... parameterTypes) {
        try {
            return clazz.getMethod(name, parameterTypes);
        } catch (NoSuchMethodException e) {
            return null;
        }
    }
}
