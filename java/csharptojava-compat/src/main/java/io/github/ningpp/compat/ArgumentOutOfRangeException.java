package io.github.ningpp.compat;

/**
 * Replacement for System.ArgumentOutOfRangeException.
 * Extends ArgumentException to preserve the C# type hierarchy
 * (ArgumentOutOfRangeException -> ArgumentException -> Exception)
 * and keep the ParamName property available.
 */
public class ArgumentOutOfRangeException extends ArgumentException {

    public ArgumentOutOfRangeException() {
        super();
    }

    public ArgumentOutOfRangeException(String paramName) {
        super(paramName, (String) null);
    }

    public ArgumentOutOfRangeException(String message, Throwable cause) {
        super(message, cause);
    }

    /** Mirrors C# ArgumentOutOfRangeException(string paramName, string? message). */
    public ArgumentOutOfRangeException(String paramName, String message) {
        super(paramName, message);
    }

    /** Mirrors C# ArgumentOutOfRangeException(string paramName, string? message, object? actualValue). */
    public ArgumentOutOfRangeException(String paramName, String message, Object actualValue) {
        super(paramName, message);
    }

    /** Mirrors C# ArgumentOutOfRangeException(string paramName, string? message, Exception? innerException). */
    public ArgumentOutOfRangeException(String paramName, String message, Throwable cause) {
        super(paramName, message, cause);
    }

    public Object getActualValue() {
        return null;
    }
}
