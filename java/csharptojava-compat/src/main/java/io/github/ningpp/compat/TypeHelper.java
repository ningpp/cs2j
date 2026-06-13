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
}
