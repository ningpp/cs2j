package io.github.ningpp.compat;

import java.lang.reflect.Field;
import java.lang.reflect.Method;

/**
 * Reflection helpers that bridge C# to Java semantic differences.
 */
public final class ReflectionHelper {

    private ReflectionHelper() {}

    /**
     * Returns the public field with the given name, or null if not found.
     * Bridges the semantic gap between C# Type.GetField (returns null)
     * and Java Class.getField (throws NoSuchFieldException).
     */
    public static Field getField(Class<?> clazz, String name) {
        try {
            return clazz.getField(name);
        } catch (NoSuchFieldException e) {
            return null;
        }
    }

    /**
     * Returns the first declared method matching {@code name} regardless of
     * parameter types, or null if not found.  Searches all methods including
     * non-public ones, and makes the result accessible so it can be invoked
     * across package boundaries.
     * <p>
     * Bridges C# {@code Type.GetMethod(name, BindingFlags)} semantics — C#
     * finds by name only and allows invoking non-public members via
     * {@code MethodInfo.Invoke}.
     */
    public static Method getDeclaredMethodByName(Class<?> clazz, String name) {
        for (Method m : clazz.getDeclaredMethods()) {
            if (m.getName().equals(name)) {
                m.setAccessible(true);
                return m;
            }
        }
        return null;
    }

    /**
     * Returns the first public method matching {@code name} regardless of
     * parameter types, or null if not found.
     * <p>
     * Bridges C# {@code Type.GetMethod(name)} semantics when no parameter
     * types are specified.
     */
    public static Method getMethodByName(Class<?> clazz, String name) {
        for (Method m : clazz.getMethods()) {
            if (m.getName().equals(name)) {
                return m;
            }
        }
        return null;
    }
}
