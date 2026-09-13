package io.github.ningpp.compat;

/**
 * Replacement for System.ArgumentException.
 * Extends IllegalArgumentException and adds support for the ParamName property.
 */
public class ArgumentException extends IllegalArgumentException {
    private int hresult;
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

    /**
     * Mirrors C# ArgumentException(string message, string? paramName) — the FIRST
     * string is the message, the second the parameter name (matches .NET).
     */
    public ArgumentException(String message, String paramName) {
        super(message);
        this.paramName = paramName;
    }

    /**
     * Mirrors C# ArgumentException(string message, string? paramName, Exception? innerException).
     */
    public ArgumentException(String message, String paramName, Throwable cause) {
        super(message, cause);
        this.paramName = paramName;
    }

    public String getParamName() {
        return paramName;
    }

    /**
     * C# composes {@code Message} as {@code message + " (Parameter 'name')"} whenever a parameter
     * name is present, and substitutes a default message when the caller passed none. The parameter
     * name therefore has to reach the text: the tests assert on
     * {@code ArgumentNullException.ToString()}, and with a null message Java's {@code toString()}
     * was nothing but the class name. Measured: {@code Assert.Contains("json", ex.ToString())}
     * reported {@code Actual string: io.github.ningpp.compat.ArgumentNullException}.
     */
    protected static String paramSuffix(String paramName) {
        return paramName == null || paramName.isEmpty() ? "" : " (Parameter '" + paramName + "')";
    }

    public int getHResult() { return hresult; }
    public void setHResult(int value) { this.hresult = value; }
}
