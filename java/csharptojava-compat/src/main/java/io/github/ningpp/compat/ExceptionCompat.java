package io.github.ningpp.compat;

/**
 * Compatibility helpers for .NET exception APIs that do not map directly to Java.
 */
public final class ExceptionCompat {
    private ExceptionCompat() {
    }

    /**
     * Returns the cause of {@code e} as a {@link RuntimeException}, or {@code null}
     * if the cause is not a {@link RuntimeException}.
     *
     * <p>This mirrors .NET's {@code Exception.InnerException} property, which always
     * returns an {@code Exception}. In Java, {@link Throwable#getCause()} returns a
     * {@link Throwable}; this helper narrows it to the runtime exception type used by
     * the converter.</p>
     */
    public static RuntimeException getInnerException(RuntimeException e) {
        if (e == null) {
            return null;
        }
        Throwable cause = e.getCause();
        return cause instanceof RuntimeException ? (RuntimeException) cause : null;
    }

    /**
     * Returns the .NET {@code Exception.Source} value when available.
     *
     * <p>Java exceptions have no Source property, so the value is recorded in a
     * weak side table keyed by the exception instance. System.Text.Json's
     * ThrowHelper marks certain InvalidOperationException/FormatException with
     * {@code Source = "dotnet.text.json.Rethrowable"}; the serializer's
     * ReadCore/WriteCore rely on that marker to rethrow as JsonException.</p>
     */
    public static String getSource(RuntimeException e) {
        if (e == null) {
            return null;
        }
        return SOURCES.get(e);
    }

    /**
     * Mirrors .NET {@code Exception.Source = value}. The value is recorded in a
     * weak side table so it survives until the exception is garbage collected.
     */
    public static void setSource(RuntimeException e, String value) {
        if (e == null) {
            return;
        }
        SOURCES.put(e, value);
    }

    private static final java.util.Map<RuntimeException, String> SOURCES =
        java.util.Collections.synchronizedMap(new java.util.WeakHashMap<>());

    /**
     * Mirrors {@code Exception.Data} — a dictionary of caller-supplied key/value
     * pairs. The Data property of ArgumentException carries "ParamName".
     */
    public static java.util.Map<String, Object> getData(RuntimeException e) {
        java.util.Map<String, Object> data = new java.util.LinkedHashMap<>();
        if (e instanceof ArgumentException argEx) {
            String paramName = argEx.getParamName();
            if (paramName != null) {
                data.put("ParamName", paramName);
            }
        }
        return data;
    }

    /**
     * Returns the stack trace of {@code e} as a {@link String}, mirroring .NET's
     * {@code Exception.StackTrace} property.
     *
     * <p>Java's {@link Throwable#getStackTrace()} returns an array of
     * {@link StackTraceElement}s; this helper formats them into a single string.</p>
     */
    public static String getStackTrace(RuntimeException e) {
        if (e == null) {
            return null;
        }
        StackTraceElement[] trace = e.getStackTrace();
        if (trace == null || trace.length == 0) {
            return "";
        }
        StringBuilder sb = new StringBuilder();
        for (StackTraceElement element : trace) {
            sb.append("   at ").append(element.toString()).append(System.lineSeparator());
        }
        return sb.toString();
    }
}
