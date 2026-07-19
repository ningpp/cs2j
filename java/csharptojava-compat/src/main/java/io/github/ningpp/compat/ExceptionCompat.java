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
     * <p>Java exceptions do not have a direct equivalent. This helper currently returns
     * {@code null}; callers that need a non-null source should set it explicitly.</p>
     */
    public static String getSource(RuntimeException e) {
        return null;
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
