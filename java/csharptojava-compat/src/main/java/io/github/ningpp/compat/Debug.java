package io.github.ningpp.compat;

/**
 * Compat for System.Diagnostics.Debug.
 * Provides assertion and failure notification methods similar to C#'s Debug class.
 */
public final class Debug {
    private Debug() {}

    /**
     * Equivalent to Debug.Fail(message).
     * Logs the failure to stderr (matching C# Trace/Debug behavior).
     */
    public static void fail(String message) {
        System.err.println("Debug Fail: " + message);
        // Optionally throw to halt execution, matching the "fail" semantics:
        throw new AssertionError("Debug.Fail: " + message);
    }

    /**
     * Equivalent to Debug.Assert(condition).
     */
    public static void assertTrue(boolean condition) {
        if (!condition) {
            fail("Assertion failed");
        }
    }

    /**
     * Equivalent to Debug.Assert(condition, message).
     */
    public static void assertTrue(boolean condition, String message) {
        if (!condition) {
            fail(message);
        }
    }
}
