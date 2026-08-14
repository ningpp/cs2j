package io.github.ningpp.compat;

/**
 * Compat for System.IO.Pipelines.PipeWriter — minimal in-memory writer.
 */
public class PipeWriter implements IBufferWriter<Integer> {
    private final java.io.ByteArrayOutputStream buffer = new java.io.ByteArrayOutputStream();

    /** Mirrors IBufferWriter&lt;byte&gt;.GetMemory(int sizeHint). */
    @Override
    public ReadOnlyMemory<Integer> getMemory(int sizeHint) {
        return new ReadOnlyMemory<>(new byte[Math.max(sizeHint, 0)], 0, Math.max(sizeHint, 0));
    }

    /** Mirrors IBufferWriter&lt;byte&gt;.Advance(int count). */
    @Override
    public void advance(int count) {
        buffer.write(new byte[Math.max(count, 0)], 0, Math.max(count, 0));
    }

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
