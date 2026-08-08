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

    public ArgumentOutOfRangeException(String message) {
        super(message);
    }

    public ArgumentOutOfRangeException(String message, Throwable cause) {
        super(message, cause);
    }

    public ArgumentOutOfRangeException(String message, String paramName) {
        super(message, paramName);
    }

    public ArgumentOutOfRangeException(String message, String paramName, Object actualValue) {
        super(message, paramName);
    }

    public ArgumentOutOfRangeException(String message, String paramName, Throwable cause) {
        super(message, paramName, cause);
    }

    public Object getActualValue() {
        return null;
    }
}
