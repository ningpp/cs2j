package io.github.ningpp.compat;

import java.lang.reflect.Field;
import java.lang.reflect.Modifier;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.Objects;

/** Minimal NUnit-style Assert surface used by converted test code. */
public final class Assert {
    private Assert() {
    }

    public static void isTrue(boolean condition) {
        if (!condition) {
            throw new AssertionError("Expected true");
        }
    }

    public static void isTrue(boolean condition, String message) {
        if (!condition) {
            throw new AssertionError(message);
        }
    }

    public static void isTrue(boolean condition, String format, Object... args) {
        if (!condition) {
            throw new AssertionError(StringHelper.formatCs(format, args));
        }
    }

    public static void isFalse(boolean condition, String format, Object... args) {
        if (condition) {
            throw new AssertionError(StringHelper.formatCs(format, args));
        }
    }

    public static void isFalse(boolean condition) {
        isTrue(!condition);
    }

    public static void isFalse(boolean condition, String message) {
        isTrue(!condition, message);
    }

    public static void isNull(Object value) {
        isTrue(value == null);
    }

    public static void isNull(Object value, String message) {
        isTrue(value == null, message);
    }

    public static void isNull(Object value, String format, Object... args) {
        if (value != null) {
            throw new AssertionError(StringHelper.formatCs(format, args));
        }
    }

    public static void isNotNull(Object value) {
        isTrue(value != null);
    }

    public static void isNotNull(Object value, String message) {
        isTrue(value != null, message);
    }

    public static void isNotNull(Object value, String format, Object... args) {
        if (value == null) {
            throw new AssertionError(StringHelper.formatCs(format, args));
        }
    }

    public static void areEqual(Object expected, Object actual) {
        if (!valuesEqual(expected, actual)) {
            throw new AssertionError("Expected: " + expected + " but was: " + actual);
        }
    }

    public static void areEqual(Object expected, Object actual, String message) {
        if (!valuesEqual(expected, actual)) {
            throw new AssertionError(message + " Expected: " + expected + " but was: " + actual);
        }
    }

    public static void areEqual(Object expected, Object actual, String format, Object... args) {
        if (!valuesEqual(expected, actual)) {
            throw new AssertionError(StringHelper.formatCs(format, args));
        }
    }

    public static void areEqual(Object expected, Object actual, boolean ignoreCase,
            CultureInfo culture, String message) {
        if (ignoreCase && expected instanceof String left && actual instanceof String right) {
            if (!left.equalsIgnoreCase(right)) {
                throw new AssertionError(message);
            }
            return;
        }
        areEqual(expected, actual, message);
    }

    public static void areEqual(double expected, double actual, double tolerance) {
        if (Math.abs(expected - actual) > tolerance) {
            throw new AssertionError("Expected: " + expected + " but was: " + actual);
        }
    }

    public static void areEqual(double expected, double actual, double tolerance, String message) {
        if (Math.abs(expected - actual) > tolerance) {
            throw new AssertionError(message + " Expected: " + expected + " but was: " + actual);
        }
    }

    public static void areNotEqual(Object expected, Object actual) {
        if (valuesEqual(expected, actual)) {
            throw new AssertionError("Expected values to differ: " + actual);
        }
    }

    public static void areNotEqual(Object expected, Object actual, String message) {
        if (valuesEqual(expected, actual)) {
            throw new AssertionError(message);
        }
    }

    public static void areNotEqual(double expected, double actual, double tolerance) {
        if (Math.abs(expected - actual) <= tolerance) {
            throw new AssertionError("Expected: " + expected + " and: " + actual + " to differ by at most " + tolerance);
        }
    }

    public static void areNotEqual(double expected, double actual, double tolerance, String message) {
        if (Math.abs(expected - actual) <= tolerance) {
            throw new AssertionError(message + " Expected: " + expected + " and: " + actual + " to differ by at most " + tolerance);
        }
    }

    public static void areNotEqual(double expected, double actual, double tolerance, String format, Object... args) {
        if (Math.abs(expected - actual) <= tolerance) {
            throw new AssertionError(StringHelper.formatCs(format, args));
        }
    }

    public static void areEqual(double expected, double actual, double tolerance, String format, Object... args) {
        if (Math.abs(expected - actual) > tolerance) {
            throw new AssertionError(StringHelper.formatCs(format, args));
        }
    }

    /**
     * Mirrors NUnit's {@code Assert.AreEqual} value semantics for converted code:
     * numeric values compare across Java wrapper types (e.g. {@code int} vs
     * {@code long}, which NUnit treats as equal), and C# structs (marked with
     * {@link CompatValueType}) compare field-wise when they do not override
     * {@code equals}, exactly like {@code ValueType.Equals}.
     */
    private static boolean valuesEqual(Object expected, Object actual) {
        if (expected == actual) {
            return true;
        }
        if (expected == null || actual == null) {
            return false;
        }
        if (expected instanceof Number left && actual instanceof Number right) {
            return numericEquals(left, right);
        }
        if (Objects.equals(expected, actual)) {
            return true;
        }
        return structEquals(expected, actual);
    }

    private static boolean numericEquals(Number left, Number right) {
        if (left instanceof BigDecimal || right instanceof BigDecimal
                || left instanceof BigInteger || right instanceof BigInteger
                || left instanceof Double || right instanceof Double
                || left instanceof Float || right instanceof Float) {
            if (left instanceof Double || right instanceof Double
                    || left instanceof Float || right instanceof Float) {
                double l = left.doubleValue();
                double r = right.doubleValue();
                if (Double.isNaN(l) || Double.isNaN(r)) {
                    return Double.isNaN(l) && Double.isNaN(r);
                }
                return l == r;
            }
            return new BigDecimal(left.toString()).compareTo(new BigDecimal(right.toString())) == 0;
        }
        return left.longValue() == right.longValue();
    }

    private static boolean structEquals(Object expected, Object actual) {
        Class<?> type = expected.getClass();
        if (type != actual.getClass() || !type.isAnnotationPresent(CompatValueType.class)) {
            return false;
        }
        // Explicit/overridden equals already ran through Objects.equals above.
        if (hasOwnEquals(type)) {
            return false;
        }
        return fieldsEqual(expected, actual, type);
    }

    private static boolean hasOwnEquals(Class<?> type) {
        try {
            return type.getMethod("equals", Object.class).getDeclaringClass() != Object.class;
        } catch (NoSuchMethodException ignored) {
            return false;
        }
    }

    private static boolean fieldsEqual(Object left, Object right, Class<?> type) {
        Class<?> current = type;
        while (current != null && current != Object.class) {
            for (Field field : current.getDeclaredFields()) {
                if (Modifier.isStatic(field.getModifiers()) || field.isSynthetic()) {
                    continue;
                }
                try {
                    field.setAccessible(true);
                    if (!fieldValuesEqual(field.get(left), field.get(right))) {
                        return false;
                    }
                } catch (ReflectiveOperationException | RuntimeException ignored) {
                    return false;
                }
            }
            current = current.getSuperclass();
        }
        return true;
    }

    private static boolean fieldValuesEqual(Object left, Object right) {
        if (left == right) {
            return true;
        }
        if (left == null || right == null) {
            return false;
        }
        if (left instanceof Number l && right instanceof Number r) {
            return numericEquals(l, r);
        }
        Class<?> type = left.getClass();
        if (type == right.getClass()
                && type.isAnnotationPresent(CompatValueType.class)
                && !hasOwnEquals(type)) {
            return fieldsEqual(left, right, type);
        }
        return Objects.equals(left, right);
    }

    public static void areSame(Object expected, Object actual) {
        if (expected != actual) {
            throw new AssertionError("Expected same instance");
        }
    }

    public static void areSame(Object expected, Object actual, String message) {
        if (expected != actual) {
            throw new AssertionError(message);
        }
    }

    public static void areNotSame(Object expected, Object actual) {
        if (expected == actual) {
            throw new AssertionError("Expected different instances of: " + actual);
        }
    }

    public static void areNotSame(Object expected, Object actual, String message) {
        if (expected == actual) {
            throw new AssertionError(message);
        }
    }

    /** NUnit {@code Assert.IsInstanceOfType(object, Type)}. */
    public static void isInstanceOfType(Object value, Class<?> expectedType) {
        if (expectedType == null || !expectedType.isInstance(value)) {
            throw new AssertionError(
                "Expected an instance of " + (expectedType == null ? "null" : expectedType.getName())
                + " but was: " + (value == null ? "null" : value.getClass().getName()));
        }
    }

    /** NUnit {@code Assert.IsInstanceOfType(object)} — any non-null object qualifies. */
    public static void isInstanceOfType(Object value) {
        if (value == null) {
            throw new AssertionError("Expected an instance but was null");
        }
    }

    /** NUnit {@code Assert.Inconclusive(message)}. */
    public static void inconclusive(String message) {
        throw new AssertionError(message);
    }

    /** NUnit {@code Assert.Throws}: fails when the action completes normally. */
    public static Exception throwsException(ThrowingRunnable action) {
        try {
            action.run();
        } catch (Exception e) {
            return e;
        }
        throw new AssertionError("Expected an exception but none was thrown");
    }

    /**
     * An action that may throw a checked exception, so lambdas passed to
     * {@link #throwsException(ThrowingRunnable)} compile without a wrapper.
     */
    @FunctionalInterface
    public interface ThrowingRunnable {
        void run() throws Exception;
    }

    public static void fail() {
        throw new AssertionError("Assert.Fail");
    }

    public static void fail(String message) {
        throw new AssertionError(message);
    }

    public static void fail(String format, Object... args) {
        throw new AssertionError(StringHelper.formatCs(format, args));
    }
}
