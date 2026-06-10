package io.github.ningpp.compat;

/**
 * Replacement for System.ArgumentException.
 * Extends IllegalArgumentException and adds support for the ParamName property.
 */
public class ArgumentException extends IllegalArgumentException {
    private final String paramName;

    public ArgumentException() {
        super();
        this.paramName = null;
    }

    public ArgumentException(String message) {
        super(message);
        this.paramName = null;
    }

    public ArgumentException(String message, Throwable cause) {
        super(message, cause);
        this.paramName = null;
    }

    public ArgumentException(String message, String paramName) {
        super(message);
        this.paramName = paramName;
    }

    public ArgumentException(String message, String paramName, Throwable cause) {
        super(message, cause);
        this.paramName = paramName;
    }

    public String getParamName() {
        return paramName;
    }
}
