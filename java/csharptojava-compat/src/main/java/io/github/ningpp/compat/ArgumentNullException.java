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
        super((String) null, paramName);
    }

    /** Mirrors C# ArgumentNullException(string paramName, string? message). */
    public ArgumentNullException(String paramName, String message) {
        super(message, paramName);
    }

    /** Mirrors C# ArgumentNullException(string paramName, string? message, Exception? innerException). */
    public ArgumentNullException(String paramName, String message, Throwable cause) {
        super(message, paramName, cause);
    }
}
