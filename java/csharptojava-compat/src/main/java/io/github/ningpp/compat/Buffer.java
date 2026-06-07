package io.github.ningpp.compat;

import java.lang.foreign.MemorySegment;

/**
 * Mirrors System.Buffer static methods for cross-array byte-level copying
 * and byte access.  All methods operate on the underlying byte representation
 * of the array, exactly like .NET's {@code Buffer} class.
 *
 * <p>.NET's {@code Buffer.BlockCopy} copies bytes regardless of the array
 * element type.  This Java implementation emulates that behaviour by
 * calculating the correct byte offsets for each primitive array type.</p>
 */
public class Buffer {

    // ---- ByteLength ----

    /**
     * Returns the number of bytes in the specified array.
     *
     * @param array the array whose byte length is needed
     * @return the number of bytes in the array
     * @throws NullPointerException if {@code array} is null
     */
    public static int byteLength(Object array) {
        if (array == null) {
            throw new NullPointerException("array");
        }
        if (array instanceof byte[]) {
            return ((byte[]) array).length;
        } else if (array instanceof char[]) {
            return ((char[]) array).length * 2;
        } else if (array instanceof short[]) {
            return ((short[]) array).length * 2;
        } else if (array instanceof int[]) {
            return ((int[]) array).length * 4;
        } else if (array instanceof long[]) {
            return ((long[]) array).length * 8;
        } else if (array instanceof float[]) {
            return ((float[]) array).length * 4;
        } else if (array instanceof double[]) {
            return ((double[]) array).length * 8;
        } else if (array instanceof boolean[]) {
            return ((boolean[]) array).length;
        } else {
            throw new IllegalArgumentException("Unsupported array type: " + array.getClass());
        }
    }

    // ---- GetByte ----

    /**
     * Gets the byte at the specified byte offset in the specified array.
     *
     * @param array the array containing the byte to retrieve
     * @param index the byte offset (zero-based) of the byte to get
     * @return the byte at the specified offset
     * @throws NullPointerException if {@code array} is null
     * @throws IndexOutOfBoundsException if {@code index} is out of range
     */
    public static byte getByte(Object array, int index) {
        if (array == null) {
            throw new NullPointerException("array");
        }
        if (index < 0) {
            throw new IndexOutOfBoundsException("index");
        }

        if (array instanceof byte[]) {
            byte[] a = (byte[]) array;
            if (index >= a.length) throw new IndexOutOfBoundsException();
            return a[index];
        } else if (array instanceof char[]) {
            char[] a = (char[]) array;
            int elemIndex = index / 2;
            int byteInElem = index % 2;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            return (byte) (a[elemIndex] >> (byteInElem * 8));
        } else if (array instanceof short[]) {
            short[] a = (short[]) array;
            int elemIndex = index / 2;
            int byteInElem = index % 2;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            return (byte) (a[elemIndex] >> (byteInElem * 8));
        } else if (array instanceof int[]) {
            int[] a = (int[]) array;
            int elemIndex = index / 4;
            int byteInElem = index % 4;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            return (byte) (a[elemIndex] >> (byteInElem * 8));
        } else if (array instanceof long[]) {
            long[] a = (long[]) array;
            int elemIndex = index / 8;
            int byteInElem = index % 8;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            return (byte) (a[elemIndex] >> (byteInElem * 8));
        } else if (array instanceof float[]) {
            float[] a = (float[]) array;
            int elemIndex = index / 4;
            int byteInElem = index % 4;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            int bits = Float.floatToIntBits(a[elemIndex]);
            return (byte) (bits >> (byteInElem * 8));
        } else if (array instanceof double[]) {
            double[] a = (double[]) array;
            int elemIndex = index / 8;
            int byteInElem = index % 8;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            long bits = Double.doubleToLongBits(a[elemIndex]);
            return (byte) (bits >> (byteInElem * 8));
        } else if (array instanceof boolean[]) {
            boolean[] a = (boolean[]) array;
            if (index >= a.length) throw new IndexOutOfBoundsException();
            return a[index] ? (byte) 1 : (byte) 0;
        } else {
            throw new IllegalArgumentException("Unsupported array type: " + array.getClass());
        }
    }

    // ---- SetByte ----

    /**
     * Sets the byte at the specified byte offset in the specified array
     * to the specified value.
     *
     * @param array the array containing the byte to set
     * @param index the byte offset (zero-based) at which to set the byte
     * @param value the byte value to set
     * @throws NullPointerException if {@code array} is null
     * @throws IndexOutOfBoundsException if {@code index} is out of range
     */
    public static void setByte(Object array, int index, byte value) {
        if (array == null) {
            throw new NullPointerException("array");
        }
        if (index < 0) {
            throw new IndexOutOfBoundsException("index");
        }

        if (array instanceof byte[]) {
            byte[] a = (byte[]) array;
            if (index >= a.length) throw new IndexOutOfBoundsException();
            a[index] = value;
        } else if (array instanceof char[]) {
            char[] a = (char[]) array;
            int elemIndex = index / 2;
            int byteInElem = index % 2;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            int mask = 0xFF << (byteInElem * 8);
            int cleared = a[elemIndex] & ~mask;
            a[elemIndex] = (char) (cleared | ((value & 0xFF) << (byteInElem * 8)));
        } else if (array instanceof short[]) {
            short[] a = (short[]) array;
            int elemIndex = index / 2;
            int byteInElem = index % 2;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            int mask = 0xFF << (byteInElem * 8);
            int cleared = a[elemIndex] & ~mask;
            a[elemIndex] = (short) (cleared | ((value & 0xFF) << (byteInElem * 8)));
        } else if (array instanceof int[]) {
            int[] a = (int[]) array;
            int elemIndex = index / 4;
            int byteInElem = index % 4;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            long mask = 0xFFL << (byteInElem * 8);
            long cleared = a[elemIndex] & ~mask;
            a[elemIndex] = (int) (cleared | ((value & 0xFFL) << (byteInElem * 8)));
        } else if (array instanceof long[]) {
            long[] a = (long[]) array;
            int elemIndex = index / 8;
            int byteInElem = index % 8;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            long mask = 0xFFL << (byteInElem * 8);
            long cleared = a[elemIndex] & ~mask;
            a[elemIndex] = cleared | ((value & 0xFFL) << (byteInElem * 8));
        } else if (array instanceof float[]) {
            float[] a = (float[]) array;
            int elemIndex = index / 4;
            int byteInElem = index % 4;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            int bits = Float.floatToIntBits(a[elemIndex]);
            long mask = 0xFFL << (byteInElem * 8);
            long cleared = bits & ~mask;
            bits = (int) (cleared | ((value & 0xFFL) << (byteInElem * 8)));
            a[elemIndex] = Float.intBitsToFloat(bits);
        } else if (array instanceof double[]) {
            double[] a = (double[]) array;
            int elemIndex = index / 8;
            int byteInElem = index % 8;
            if (elemIndex >= a.length) throw new IndexOutOfBoundsException();
            long bits = Double.doubleToLongBits(a[elemIndex]);
            long mask = 0xFFL << (byteInElem * 8);
            long cleared = bits & ~mask;
            bits = cleared | ((value & 0xFFL) << (byteInElem * 8));
            a[elemIndex] = Double.longBitsToDouble(bits);
        } else if (array instanceof boolean[]) {
            boolean[] a = (boolean[]) array;
            if (index >= a.length) throw new IndexOutOfBoundsException();
            a[index] = (value != 0);
        } else {
            throw new IllegalArgumentException("Unsupported array type: " + array.getClass());
        }
    }

    // ---- BlockCopy ----

    /**
     * Copies a specified number of bytes from a source array starting at
     * a particular offset to a destination array starting at a particular
     * offset.  This method copies bytes; it does <em>not</em> respect
     * element boundaries the way {@code System.arraycopy} does.
     *
     * <p>The .NET equivalent is:
     * {@code Buffer.BlockCopy(src, srcOffset, dst, dstOffset, count)}</p>
     *
     * @param src       the source array
     * @param srcOffset the zero-based byte offset in {@code src} at which
     *                  copying begins
     * @param dst       the destination array
     * @param dstOffset the zero-based byte offset in {@code dst} at which
     *                  copying begins
     * @param count     the number of bytes to copy
     * @throws NullPointerException      if {@code src} or {@code dst} is null
     * @throws IndexOutOfBoundsException if any offset or count is out of range
     */
    public static void blockCopy(Object src, int srcOffset,
                                 Object dst, int dstOffset,
                                 int count) {
        if (src == null) {
            throw new NullPointerException("src");
        }
        if (dst == null) {
            throw new NullPointerException("dst");
        }
        if (srcOffset < 0 || dstOffset < 0 || count < 0) {
            throw new IndexOutOfBoundsException();
        }

        // Fast path: both are byte[] – just use System.arraycopy
        if (src instanceof byte[] && dst instanceof byte[]) {
            byte[] s = (byte[]) src;
            byte[] d = (byte[]) dst;
            if (srcOffset + count > s.length || dstOffset + count > d.length) {
                throw new IndexOutOfBoundsException();
            }
            System.arraycopy(s, srcOffset, d, dstOffset, count);
            return;
        }

        // General path: copy byte-by-byte using getByte/setByte
        if (srcOffset + count > byteLength(src)) {
            throw new IndexOutOfBoundsException("src");
        }
        if (dstOffset + count > byteLength(dst)) {
            throw new IndexOutOfBoundsException("dst");
        }

        for (int i = 0; i < count; i++) {
            byte b = getByte(src, srcOffset + i);
            setByte(dst, dstOffset + i, b);
        }
    }

    // ---- MemoryCopy (unsafe / pointer-based – not directly portable) ----
    // .NET's MemoryCopy works with void* pointers.  In Java we provide an
    // overload that copies between two arrays so that converted code can
    // still compile.  For true off-heap memory, callers should use
    // java.lang.foreign.MemorySegment instead.

    /**
     * Copies bytes from a source array to a destination array.
     * This is a Java-compatible replacement for the unsafe
     * {@code Buffer.MemoryCopy} that .NET uses with pointers.
     *
     * @param source      the source array
     * @param destination the destination array
     * @param count       the number of bytes to copy
     */
    public static void memoryCopy(Object source, Object destination, long count) {
        if (source == null) {
            throw new NullPointerException("source");
        }
        if (destination == null) {
            throw new NullPointerException("destination");
        }
        int intCount = (int) count;
        if (count != intCount) {
            throw new IllegalArgumentException("count exceeds int range");
        }
        blockCopy(source, 0, destination, 0, intCount);
    }

    /**
     * Copies bytes between two MemorySegments.
     * This overload matches .NET's {@code Buffer.MemoryCopy(void*, void*, long, long)}
     * where the fourth parameter ({@code sourceBytesToCopy}) determines the actual
     * number of bytes to copy.
     *
     * @param source            the source MemorySegment
     * @param destination       the destination MemorySegment
     * @param sourceSizeBytes   the size of the source buffer in bytes (unused, kept for API compatibility)
     * @param sourceBytesToCopy the number of bytes to copy
     */
    public static void memoryCopy(MemorySegment source, MemorySegment destination, long sourceSizeBytes, long sourceBytesToCopy) {
        MemorySegment.copy(source, 0L, destination, 0L, sourceBytesToCopy);
    }
}
