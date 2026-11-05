package io.github.ningpp.compat;

/** Minimal NUnit/MSTest StringAssert surface used by converted test code. */
public final class StringAssert {
    private StringAssert() {
    }

    public static void startsWith(String value, String expected) {
        startsWith(value, expected, null);
    }

    public static void startsWith(String value, String expected, String message) {
        if (value == null || expected == null || !value.startsWith(expected)) {
            throw new AssertionError(prefixed(message)
                + "Expected string to start with: " + expected + " but was: " + value);
        }
    }

    public static void endsWith(String value, String expected) {
        endsWith(value, expected, null);
    }

    public static void endsWith(String value, String expected, String message) {
        if (value == null || expected == null || !value.endsWith(expected)) {
            throw new AssertionError(prefixed(message)
                + "Expected string to end with: " + expected + " but was: " + value);
        }
    }

    public static void contains(String value, String expected) {
        contains(value, expected, null);
    }

    public static void contains(String value, String expected, String message) {
        if (value == null || expected == null || !value.contains(expected)) {
            throw new AssertionError(prefixed(message)
                + "Expected string to contain: " + expected + " but was: " + value);
        }
    }

    private static String prefixed(String message) {
        return message == null || message.isEmpty() ? "" : message + " ";
    }
}
