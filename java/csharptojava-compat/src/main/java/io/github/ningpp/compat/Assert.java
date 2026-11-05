package io.github.ningpp.compat;

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
        if (!Objects.equals(expected, actual)) {
            throw new AssertionError("Expected: " + expected + " but was: " + actual);
        }
    }

    public static void areEqual(Object expected, Object actual, String message) {
        if (!Objects.equals(expected, actual)) {
            throw new AssertionError(message + " Expected: " + expected + " but was: " + actual);
        }
    }

    public static void areEqual(Object expected, Object actual, String format, Object... args) {
        if (!Objects.equals(expected, actual)) {
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
        if (Objects.equals(expected, actual)) {
            throw new AssertionError("Expected values to differ: " + actual);
        }
    }

    public static void areNotEqual(Object expected, Object actual, String message) {
        if (Objects.equals(expected, actual)) {
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
