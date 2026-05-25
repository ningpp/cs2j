package io.github.ningpp.compat;

import java.lang.reflect.Field;

/**
 * Reflection helpers that bridge C# to Java semantic differences.
 * In C#, Type.GetField returns null when the field is not found;
 * in Java, Class.getField throws NoSuchFieldException.
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
}
