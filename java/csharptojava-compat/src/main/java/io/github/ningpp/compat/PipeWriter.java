package io.github.ningpp.compat;

/**
 * Compat for System.IO.Pipelines.PipeWriter — minimal in-memory writer.
 */
public class PipeWriter {
    private final java.io.ByteArrayOutputStream buffer = new java.io.ByteArrayOutputStream();

    public void write(byte[] data, int offset, int count) {
        buffer.write(data, offset, count);
    }

    public void write(byte[] data) {
        buffer.write(data, 0, data.length);
    }

    public void write(ReadOnlySpan<Integer> span) {
        for (int i = 0; i < span.length(); i++) {
            buffer.write(span.get(i) & 0xFF);
        }
    }

    public byte[] toArray() {
        return buffer.toByteArray();
    }

    public int getLength() {
        return buffer.size();
    }

    public void flush() {
    }

    public void complete() {
    }
}
