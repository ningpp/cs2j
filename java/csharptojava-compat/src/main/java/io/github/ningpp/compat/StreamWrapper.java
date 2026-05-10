package io.github.ningpp.compat;

import java.io.InputStream;
import java.io.OutputStream;

/**
 * Wraps either an InputStream or OutputStream, bridging the gap between
 * C# System.IO.Stream (which is bidirectional) and Java's separate
 * InputStream / OutputStream hierarchy.
 * <p>
 * Callers that need the underlying stream for read or write should check
 * which type is available via {@link #inputStream()} /
 * {@link #outputStream()} or use the type-query methods.
 */
public class StreamWrapper {

    private final InputStream inputStream;
    private final OutputStream outputStream;

    private StreamWrapper(InputStream in, OutputStream out) {
        this.inputStream = in;
        this.outputStream = out;
    }

    public static StreamWrapper of(InputStream in) {
        return new StreamWrapper(in, null);
    }

    public static StreamWrapper of(OutputStream out) {
        return new StreamWrapper(null, out);
    }

    public InputStream inputStream() {
        if (inputStream == null)
            throw new IllegalStateException("StreamWrapper does not hold an InputStream");
        return inputStream;
    }

    public OutputStream outputStream() {
        if (outputStream == null)
            throw new IllegalStateException("StreamWrapper does not hold an OutputStream");
        return outputStream;
    }

    public boolean isInputStream() { return inputStream != null; }
    public boolean isOutputStream() { return outputStream != null; }
}
