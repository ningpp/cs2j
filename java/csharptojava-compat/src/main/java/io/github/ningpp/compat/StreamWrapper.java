package io.github.ningpp.compat;

import java.io.IOException;
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
public class StreamWrapper implements AutoCloseable {

    private final InputStream inputStream;
    private final OutputStream outputStream;
    private final String name;

    protected StreamWrapper(InputStream in, OutputStream out) {
        this(in, out, null);
    }

    protected StreamWrapper(InputStream in, OutputStream out, String name) {
        this.inputStream = in;
        this.outputStream = out;
        this.name = name;
    }

    public static StreamWrapper of(InputStream in) {
        return new StreamWrapper(in, null);
    }

    public static StreamWrapper of(OutputStream out) {
        return new StreamWrapper(null, out);
    }

    public static StreamWrapper of(StreamWrapper stream) {
        return stream;
    }

    public static StreamWrapper open(String path, int fileMode) {
        return FileHelper.open(path, fileMode);
    }

    public static StreamWrapper open(String path, int fileMode, int fileAccess) {
        return FileHelper.open(path, fileMode, fileAccess);
    }

    public static StreamWrapper of(InputStream in, String name) {
        return new StreamWrapper(in, null, name);
    }

    public static StreamWrapper of(OutputStream out, String name) {
        return new StreamWrapper(null, out, name);
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

    /** Mirrors C# Stream.CanSeek */
    public boolean getCanSeek() {
        return false;
    }

    public String getName() { return name == null ? "" : name; }

    public int read() {
        try { return inputStream().read(); }
        catch (IOException e) { throw new java.io.UncheckedIOException(e); }
    }

    public int readByte() {
        return read();
    }

    public int read(byte[] buffer) {
        try { return inputStream().read(buffer); }
        catch (IOException e) { throw new java.io.UncheckedIOException(e); }
    }

    public int read(byte[] buffer, int offset, int count) {
        try {
            int result = inputStream().read(buffer, offset, count);
            return result == -1 ? 0 : result;
        } catch (IOException e) { throw new java.io.UncheckedIOException(e); }
    }

    public void write(int value) {
        try { outputStream().write(value); }
        catch (IOException e) { throw new java.io.UncheckedIOException(e); }
    }

    public void write(byte[] buffer) {
        try { outputStream().write(buffer); }
        catch (IOException e) { throw new java.io.UncheckedIOException(e); }
    }

    public void write(byte[] buffer, int offset, int count) {
        try { outputStream().write(buffer, offset, count); }
        catch (IOException e) { throw new java.io.UncheckedIOException(e); }
    }

    public void flush() {
        try {
            if (outputStream != null) outputStream.flush();
        } catch (IOException e) { throw new java.io.UncheckedIOException(e); }
    }

    /** Mirrors C# Stream.CopyTo(Stream destination) */
    public void copyTo(StreamWrapper destination) {
        try {
            byte[] buffer = new byte[8192];
            int bytesRead;
            InputStream in = inputStream();
            OutputStream out = destination.outputStream();
            while ((bytesRead = in.read(buffer)) != -1) {
                out.write(buffer, 0, bytesRead);
            }
        } catch (IOException e) {
            throw new java.io.UncheckedIOException(e);
        }
    }

    public long getPosition() {
        throw new UnsupportedOperationException("This stream does not support Position");
    }

    public void setPosition(long value) {
        throw new UnsupportedOperationException("This stream does not support Position");
    }

    public long getLength() {
        throw new UnsupportedOperationException("This stream does not support Length");
    }

    @Override
    public void close() {
        try {
            if (inputStream != null) inputStream.close();
            if (outputStream != null) outputStream.close();
        } catch (IOException e) { throw new java.io.UncheckedIOException(e); }
    }
}
