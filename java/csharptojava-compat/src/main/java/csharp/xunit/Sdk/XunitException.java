package csharp.xunit.Sdk;

public class XunitException extends AssertionError {
    public XunitException(String message) {
        super(message);
    }

    public XunitException(String message, Throwable cause) {
        super(message, cause);
    }
}
