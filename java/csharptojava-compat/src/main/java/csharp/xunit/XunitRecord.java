package csharp.xunit;

/**
 * Xunit.Record compatibility layer for Java.
 * <p>
 * C# Xunit.Record.Exception(Action) captures the exception thrown by an
 * action, or null when the action completes without throwing.
 */
public final class XunitRecord {

    private XunitRecord() {}

    /**
     * C# Xunit.Record.Exception(Action) — runs the action and returns the
     * thrown RuntimeException, or null when the action does not throw.
     */
    public static RuntimeException exception(Runnable action) {
        try {
            action.run();
            return null;
        } catch (RuntimeException e) {
            return e;
        }
    }
}
