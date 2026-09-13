package io.github.ningpp.compat;

/**
 * Replacement for System.ArgumentNullException.
 * Extends ArgumentException to preserve the C# type hierarchy
 * (ArgumentNullException -> ArgumentException -> Exception).
 * This ensures generic constraints like {@code <T extends ArgumentException>}
 * are satisfied when T = ArgumentNullException.
 */
public class ArgumentNullException extends ArgumentException {

    public ArgumentNullException() {
        super();
    }

    public ArgumentNullException(String paramName) {
        // C#: ArgumentNullException(string paramName) uses ArgumentNull_Generic and appends the
        // parameter name, so Message reads "Value cannot be null. (Parameter 'options')".
        super("Value cannot be null." + paramSuffix(paramName), paramName);
    }

    /** Mirrors C# ArgumentNullException(string paramName, string? message). */
    public ArgumentNullException(String paramName, String message) {
        super(message == null ? "Value cannot be null." + paramSuffix(paramName)
            : message + paramSuffix(paramName), paramName);
    }

    /** Mirrors C# ArgumentNullException(string paramName, string? message, Exception? innerException). */
    public ArgumentNullException(String paramName, String message, Throwable cause) {
        super(message == null ? "Value cannot be null." + paramSuffix(paramName)
            : message + paramSuffix(paramName), paramName, cause);
    }
}
