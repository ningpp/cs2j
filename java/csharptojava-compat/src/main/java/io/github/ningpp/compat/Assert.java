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

    public static void isNotNull(Object value) {
        isTrue(value != null);
    }

    public static void isNotNull(Object value, String message) {
        isTrue(value != null, message);
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

    public static void fail() {
        throw new AssertionError("Assert.Fail");
    }

    public static void fail(String message) {
        throw new AssertionError(message);
    }
}
