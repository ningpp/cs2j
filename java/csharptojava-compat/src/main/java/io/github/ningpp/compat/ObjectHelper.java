package io.github.ningpp.compat;

import java.lang.reflect.Method;

/**
 * Mirrors .NET object.ToString() semantics for values whose C# type declares no
 * ToString override (or whose override is compiled out, e.g. inside #if DEBUG):
 * .NET returns the runtime type's FullName, while Java's default Object.toString()
 * appends "@hash". Types that DO override toString() keep virtual dispatch.
 */
public final class ObjectHelper {

    private ObjectHelper() {
    }

    public static String objectToString(Object value) {
        if (value == null) {
            throw new NullPointerException("value");
        }
        try {
            Method toStringMethod = value.getClass().getMethod("toString");
            if (toStringMethod.getDeclaringClass() == Object.class) {
                return value.getClass().getName();
            }
        } catch (NoSuchMethodException e) {
            return value.getClass().getName();
        }
        return value.toString();
    }
}
