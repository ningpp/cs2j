package io.github.ningpp.compat;

public class TimeoutException extends SystemException {
    public TimeoutException() { super(); }
    public TimeoutException(String message) { super(message); }
    public TimeoutException(String message, Throwable cause) { super(message, cause); }
}
