package io.github.ningpp.compat;

/**
 * Compat for System.IO.EndOfStreamException. C# exceptions are unchecked from
 * the converter's perspective, and the generated code rarely declares checked
 * throws, so this extends RuntimeException (matching C# semantics where stream
 * reads do not require explicit exception handling).
 */
public class EndOfStreamException extends RuntimeException {
    public EndOfStreamException() {
        super();
    }

    public EndOfStreamException(String message) {
        super(message);
    }

    public EndOfStreamException(String message, Throwable cause) {
        super(message, cause);
    }
}
