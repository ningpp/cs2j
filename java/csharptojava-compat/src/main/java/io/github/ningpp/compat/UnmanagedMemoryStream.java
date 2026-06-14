package io.github.ningpp.compat;

import java.lang.foreign.MemorySegment;
import java.util.Arrays;

/** Minimal replacement for System.IO.UnmanagedMemoryStream over immutable bytes. */
public class UnmanagedMemoryStream extends MemoryStream {
    private final byte[] bytes;

    public UnmanagedMemoryStream(byte[] bytes) {
        super(bytes);
        this.bytes = Arrays.copyOf(bytes, bytes.length);
    }

    /** Mirrors C# UnmanagedMemoryStream.PositionPointer for resource-backed data. */
    public MemorySegment getPositionPointer() {
        long position = getPosition();
        if (position < 0 || position > bytes.length)
            throw new IllegalStateException("Position out of range: " + position);
        return MemorySegment.ofArray(bytes).asSlice(position);
    }
}
