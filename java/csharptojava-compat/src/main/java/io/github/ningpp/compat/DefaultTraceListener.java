package io.github.ningpp.compat;

public class DefaultTraceListener {
    public void fail(String message) {
        throw new AssertionError(message);
    }
}
