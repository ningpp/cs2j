package Microsoft.VisualStudio.TestTools.UnitTesting;

import java.util.Objects;

public final class Assert {
    private Assert() {}

    public static void areSame(Object expected, Object actual, String message) {
        if (expected != actual) {
            fail(format(message, "Expected references to be identical."));
        }
    }

    public static void areEqual(Object expected, Object actual, String message) {
        if (!Objects.equals(expected, actual)) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void isTrue(boolean condition, String message) {
        if (!condition) {
            fail(format(message, "Expected condition to be true."));
        }
    }

    public static void isFalse(boolean condition, String message) {
        if (condition) {
            fail(format(message, "Expected condition to be false."));
        }
    }

    public static void isNotNull(Object value, String message) {
        if (value == null) {
            fail(format(message, "Expected value to be non-null."));
        }
    }

    public static void fail(String message) {
        throw new AssertionError(message == null ? "Assertion failed." : message);
    }

    private static String format(String message, String fallback) {
        return message == null || message.isEmpty() ? fallback : message;
    }
}
