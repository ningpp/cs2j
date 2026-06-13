package io.github.ningpp.compat;

import java.io.InputStream;
import java.io.OutputStream;
import java.util.Arrays;

/** Replacement for System.IO.MemoryStream with shared read/write position semantics. */
public class MemoryStream extends StreamWrapper {
    private byte[] buffer;
    private int length;
    private int position;

    private final InputStream inputAdapter = new InputStream() {
        @Override
        public int read() {
            return MemoryStream.this.read();
        }

        @Override
        public int read(byte[] b, int off, int len) {
            if (position >= length) return -1;
            int n = Math.min(len, length - position);
            System.arraycopy(buffer, position, b, off, n);
            position += n;
            return n;
        }
    };

    private final OutputStream outputAdapter = new OutputStream() {
        @Override
        public void write(int b) {
            MemoryStream.this.write(b);
        }

        @Override
        public void write(byte[] b, int off, int len) {
            MemoryStream.this.write(b, off, len);
        }

        @Override
        public void flush() {
            MemoryStream.this.flush();
        }
    };

    public MemoryStream() {
        super(null, null);
        this.buffer = new byte[0];
    }

    public MemoryStream(byte[] buffer) {
        super(null, null);
        this.buffer = Arrays.copyOf(buffer, buffer.length);
        this.length = buffer.length;
    }

    /** Mirrors C# new MemoryStream(byte[], int, int) */
    public MemoryStream(byte[] buffer, int offset, int count) {
        super(null, null);
        this.buffer = new byte[count];
        System.arraycopy(buffer, offset, this.buffer, 0, count);
        this.length = count;
    }

    @Override
    public InputStream inputStream() {
        return inputAdapter;
    }

    @Override
    public OutputStream outputStream() {
        return outputAdapter;
    }

    @Override
    public boolean isInputStream() {
        return true;
    }

    @Override
    public boolean isOutputStream() {
        return true;
    }

    @Override
    public int read() {
        if (position >= length) return -1;
        return buffer[position++] & 0xff;
    }

    @Override
    public int read(byte[] target) {
        return read(target, 0, target.length);
    }

    @Override
    public int read(byte[] target, int offset, int count) {
        if (position >= length) return 0;
        int n = Math.min(count, length - position);
        System.arraycopy(buffer, position, target, offset, n);
        position += n;
        return n;
    }

    @Override
    public void write(int value) {
        ensureCapacity(position + 1);
        buffer[position++] = (byte)value;
        if (position > length) length = position;
    }

    @Override
    public void write(byte[] source) {
        write(source, 0, source.length);
    }

    @Override
    public void write(byte[] source, int offset, int count) {
        ensureCapacity(position + count);
        System.arraycopy(source, offset, buffer, position, count);
        position += count;
        if (position > length) length = position;
    }

    public void writeByte(int value) {
        write(value);
    }

    @Override
    public long getPosition() {
        return position;
    }

    @Override
    public void setPosition(long value) {
        if (value < 0 || value > Integer.MAX_VALUE)
            throw new IllegalArgumentException("Position out of range: " + value);
        position = (int)value;
    }

    @Override
    public long getLength() {
        return length;
    }

    public void setLength(long value) {
        if (value < 0 || value > Integer.MAX_VALUE)
            throw new IllegalArgumentException("Length out of range: " + value);
        int newLength = (int)value;
        ensureCapacity(newLength);
        if (newLength > length) Arrays.fill(buffer, length, newLength, (byte)0);
        length = newLength;
        if (position > length) position = length;
    }

    public byte[] toArray() {
        return Arrays.copyOf(buffer, length);
    }

    @Override
    public void flush() {
    }

    @Override
    public void close() {
    }

    private void ensureCapacity(int capacity) {
        if (capacity <= buffer.length) return;
        int next = Math.max(capacity, Math.max(256, buffer.length * 2));
        buffer = Arrays.copyOf(buffer, next);
    }
}
