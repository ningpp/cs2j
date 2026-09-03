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

    /**
     * Mirrors C# object.Equals(object) semantics.
     * C# considers 0.0 == -0.0 for double/float, but Java's Double.equals
     * and Float.equals compare bit patterns and return false for +0.0 vs -0.0.
     * This method special-cases Double and Float to match C# behavior.
     */
    public static boolean objectEquals(Object a, Object b) {
        if (a == null && b == null) {
            return true;
        }
        if (a == null || b == null) {
            return false;
        }
        if (a instanceof Double && b instanceof Double) {
            double da = (Double) a;
            double db = (Double) b;
            // C# treats +0.0 and -0.0 as equal; Java's Double.equals does not.
            if (da == 0.0 && db == 0.0) {
                return true;
            }
            return da == db;
        }
        if (a instanceof Float && b instanceof Float) {
            float fa = (Float) a;
            float fb = (Float) b;
            if (fa == 0.0f && fb == 0.0f) {
                return true;
            }
            return fa == fb;
        }
        return a.equals(b);
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
