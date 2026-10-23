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
            // C# double.Equals(object) returns true when (a == b) || (IsNaN(a) && IsNaN(b)).
            // This means: +0.0 Equals -0.0 is true (like Java ==),
            //              NaN Equals NaN is true (unlike Java ==).
            if (da == db) {
                return true;
            }
            return Double.isNaN(da) && Double.isNaN(db);
        }
        if (a instanceof Float && b instanceof Float) {
            float fa = (Float) a;
            float fb = (Float) b;
            // Same as Double: float.Equals returns true for NaN == NaN.
            if (fa == fb) {
                return true;
            }
            return Float.isNaN(fa) && Float.isNaN(fb);
        }
        return a.equals(b);
    }

    /**
     * Mirrors C# object.ReferenceEquals(a, b): reference identity only,
     * never value equality. Both null yields true.
     */
    public static boolean referenceEquals(Object a, Object b) {
        return a == b;
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

    /**
     * Shallow field-wise copy equivalent to C# Object.MemberwiseClone():
     * allocates a fresh instance of the runtime class and copies every
     * non-static field (inherited fields included) from the source.
     */
    public static Object __memberwiseClone(Object source) {
        Class<?> cls = source.getClass();
        Object copy = allocateShallow(cls);
        for (Class<?> c = cls; c != null && c != Object.class; c = c.getSuperclass()) {
            for (java.lang.reflect.Field f : c.getDeclaredFields()) {
                if (java.lang.reflect.Modifier.isStatic(f.getModifiers())) {
                    continue;
                }
                try {
                    f.setAccessible(true);
                    f.set(copy, f.get(source));
                } catch (ReflectiveOperationException e) {
                    throw new RuntimeException("__memberwiseClone failed on field "
                        + c.getName() + "." + f.getName(), e);
                }
            }
        }
        return copy;
    }

    private static Object allocateShallow(Class<?> cls) {
        try {
            java.lang.reflect.Constructor<?> noArg = cls.getDeclaredConstructor();
            noArg.setAccessible(true);
            return noArg.newInstance();
        } catch (ReflectiveOperationException ignored) {
            // fall through to parameterized constructors
        }
        for (java.lang.reflect.Constructor<?> ctor : cls.getDeclaredConstructors()) {
            try {
                ctor.setAccessible(true);
                Class<?>[] parameterTypes = ctor.getParameterTypes();
                Object[] args = new Object[parameterTypes.length];
                for (int i = 0; i < parameterTypes.length; i++) {
                    args[i] = defaultValue(parameterTypes[i]);
                }
                return ctor.newInstance(args);
            } catch (ReflectiveOperationException ignored) {
                // try the next constructor
            }
        }
        throw new RuntimeException("__memberwiseClone: no usable constructor on " + cls.getName());
    }

    private static Object defaultValue(Class<?> type) {
        if (!type.isPrimitive()) {
            return null;
        }
        if (type == boolean.class) return Boolean.FALSE;
        if (type == byte.class) return (byte) 0;
        if (type == short.class) return (short) 0;
        if (type == int.class) return 0;
        if (type == long.class) return 0L;
        if (type == float.class) return 0.0f;
        if (type == double.class) return 0.0d;
        if (type == char.class) return '\0';
        return null;
    }
}
