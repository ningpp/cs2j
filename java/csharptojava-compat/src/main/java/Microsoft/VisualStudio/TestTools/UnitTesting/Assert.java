package Microsoft.VisualStudio.TestTools.UnitTesting;

import java.util.Objects;

public final class Assert {
    private Assert() {}

    public static void areSame(Object expected, Object actual, String message) {
        if (expected != actual) {
            fail(format(message, "Expected references to be identical."));
        }
    }

    public static void areSame(Object expected, Object actual, String message, Object... args) {
        areSame(expected, actual, formatMessage(message, args));
    }

    public static void areNotSame(Object notExpected, Object actual, String message) {
        if (notExpected == actual) {
            fail(format(message, "Expected references to be different."));
        }
    }

    public static void areNotSame(Object notExpected, Object actual, String message, Object... args) {
        areNotSame(notExpected, actual, formatMessage(message, args));
    }

    public static void areEqual(Object expected, Object actual, String message) {
        if (!Objects.equals(expected, actual)) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void areEqual(Object expected, Object actual, String message, Object... args) {
        areEqual(expected, actual, formatMessage(message, args));
    }

    public static void areEqual(double expected, double actual, double delta, String message) {
        if (Double.isNaN(expected) && Double.isNaN(actual)) {
            return;
        }
        if (Math.abs(expected - actual) > delta) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void areEqual(double expected, double actual, double delta, String message, Object... args) {
        areEqual(expected, actual, delta, formatMessage(message, args));
    }

    public static void areEqual(String expected, String actual, boolean ignoreCase, io.github.ningpp.compat.CultureInfo culture, String message) {
        boolean equal = ignoreCase
            ? (expected == null ? actual == null : actual != null && expected.equalsIgnoreCase(actual))
            : Objects.equals(expected, actual);
        if (!equal) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void areEqual(String expected, String actual, boolean ignoreCase, io.github.ningpp.compat.CultureInfo culture, String message, Object... args) {
        areEqual(expected, actual, ignoreCase, culture, formatMessage(message, args));
    }

    public static void areNotEqual(Object notExpected, Object actual, String message) {
        if (Objects.equals(notExpected, actual)) {
            fail(format(message, "Did not expect <" + actual + ">."));
        }
    }

    public static void areNotEqual(Object notExpected, Object actual, String message, Object... args) {
        areNotEqual(notExpected, actual, formatMessage(message, args));
    }

    public static void isTrue(boolean condition, String message) {
        if (!condition) {
            fail(format(message, "Expected condition to be true."));
        }
    }

    public static void isTrue(boolean condition, String message, Object... args) {
        isTrue(condition, formatMessage(message, args));
    }

    public static void isFalse(boolean condition, String message) {
        if (condition) {
            fail(format(message, "Expected condition to be false."));
        }
    }

    public static void isFalse(boolean condition, String message, Object... args) {
        isFalse(condition, formatMessage(message, args));
    }

    public static void isNull(Object value, String message) {
        if (value != null) {
            fail(format(message, "Expected value to be null."));
        }
    }

    public static void isNull(Object value, String message, Object... args) {
        isNull(value, formatMessage(message, args));
    }

    public static void isNotNull(Object value, String message) {
        if (value == null) {
            fail(format(message, "Expected value to be non-null."));
        }
    }

    public static void isNotNull(Object value, String message, Object... args) {
        isNotNull(value, formatMessage(message, args));
    }

    public static void fail(String message) {
        throw new UnitTestAssertException(message == null ? "Assertion failed." : message);
    }

    public static void fail(String message, Object... args) {
        fail(formatMessage(message, args));
    }

    private static String format(String message, String fallback) {
        return message == null || message.isEmpty() ? fallback : message;
    }

    private static String formatMessage(String message, Object... args) {
        if (message == null || args == null || args.length == 0) {
            return message;
        }

        String format = message;
        for (int i = 0; i < args.length; i++) {
            format = format.replace("{" + i + "}", "%s");
        }
        return String.format(format, args);
    }
}
