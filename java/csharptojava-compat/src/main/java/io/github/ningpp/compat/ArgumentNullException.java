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
        super(null, paramName);
    }

    public ArgumentNullException(String message, String paramName) {
        super(message, paramName);
    }

    public ArgumentNullException(String message, String paramName, Throwable cause) {
        super(message, paramName, cause);
    }
}
