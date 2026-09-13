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
        // C#: ArgumentOutOfRangeException(string paramName) uses ArgumentOutOfRange_Generic and
        // appends the parameter name. Measured: NumberHandlingTests expects "value" in ToString().
        super("Specified argument was out of the range of valid values." + paramSuffix(paramName), paramName);
    }

    public ArgumentOutOfRangeException(String message, Throwable cause) {
        super(message, cause);
    }

    /** Mirrors C# ArgumentOutOfRangeException(string paramName, string? message). */
    public ArgumentOutOfRangeException(String paramName, String message) {
        super(message, paramName);
    }

    /** Mirrors C# ArgumentOutOfRangeException(string paramName, string? message, object? actualValue). */
    public ArgumentOutOfRangeException(String paramName, String message, Object actualValue) {
        super(message, paramName);
    }

    /** Mirrors C# ArgumentOutOfRangeException(string paramName, object? actualValue, string? message). */
    public ArgumentOutOfRangeException(String paramName, Object actualValue, String message) {
        super(message, paramName);
    }

    /** Mirrors C# ArgumentOutOfRangeException(string paramName, string? message, Exception? innerException). */
    public ArgumentOutOfRangeException(String paramName, String message, Throwable cause) {
        super(message, paramName, cause);
    }

    public Object getActualValue() {
        return null;
    }
}
