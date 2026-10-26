package io.github.ningpp.compat;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Compatibility bridge for the static byte-conversion methods of
 * {@code System.BitConverter}.
 *
 * <p>Multi-byte values are read in the platform's native byte order, matching
 * .NET's {@code BitConverter} behavior on the same machine.</p>
 */
public final class BitConverter {

    /**
     * C# BitConverter.IsLittleEndian. The JVM spec fixes the in-memory byte
     * order of physical memory only; the convention for this compat surface is
     * the prevalent little-endian runtime (x86/x64/ARM), matching the C# value
     * on every platform this corpus targets.
     */
    public static final boolean IsLittleEndian = true;
    private BitConverter() {
    }

    /**
     * Returns a signed 16-bit integer converted from two bytes at the specified
     * position in a byte array.
     */
    public static short toInt16(byte[] value, int startIndex) {
        return ByteBuffer.wrap(value, startIndex, 2).order(ByteOrder.nativeOrder()).getShort();
    }

    /**
     * Returns an unsigned 16-bit integer converted from two bytes at the specified
     * position in a byte array.  The value is returned as a Java {@code int} with
     * the C# {@code ushort} bit pattern preserved.
     */
    public static int toUInt16(byte[] value, int startIndex) {
        return ByteBuffer.wrap(value, startIndex, 2).order(ByteOrder.nativeOrder()).getShort() & 0xFFFF;
    }

    /**
     * Returns an unsigned 32-bit integer converted from four bytes at the specified
     * position in a byte array.  The value is returned as a Java {@code int} with
     * the C# {@code uint} bit pattern preserved.
     */
    public static int toUInt32(byte[] value, int startIndex) {
        return ByteBuffer.wrap(value, startIndex, 4).order(ByteOrder.nativeOrder()).getInt();
    }

    /**
     * Returns an unsigned 64-bit integer converted from eight bytes at the specified
     * position in a byte array.  The value is returned as a Java {@code long} with
     * the C# {@code ulong} bit pattern preserved.
     */
    public static long toUInt64(byte[] value, int startIndex) {
        return ByteBuffer.wrap(value, startIndex, 8).order(ByteOrder.nativeOrder()).getLong();
    }

    /**
     * Converts two bytes at the specified position in a byte array to a char.
     * Uses the platform's native byte order, matching .NET's BitConverter behavior.
     */
    public static char toChar(byte[] value, int startIndex) {
        return ByteBuffer.wrap(value, startIndex, 2).order(ByteOrder.nativeOrder()).getChar();
    }

    /**
     * Converts the specified double-precision floating-point number to a 64-bit
     * signed integer, preserving the exact IEEE 754 bit pattern (including
     * negative zero and NaN payloads).
     */
    public static long doubleToInt64Bits(double value) {
        return Double.doubleToRawLongBits(value);
    }

    /**
     * Converts the specified single-precision floating-point number to a 32-bit
     * signed integer, preserving the exact IEEE 754 bit pattern (including
     * negative zero and NaN payloads).
     */
    public static int singleToInt32Bits(float value) {
        return Float.floatToRawIntBits(value);
    }

    /**
     * Converts the specified 32-bit signed integer to a single-precision
     * floating-point number, preserving the exact IEEE 754 bit pattern.
     */
    public static float int32BitsToSingle(int value) {
        return Float.intBitsToFloat(value);
    }

    /**
     * Converts the specified 64-bit signed integer to a double-precision
     * floating-point number, preserving the exact IEEE 754 bit pattern.
     */
    public static double int64BitsToDouble(long value) {
        return Double.longBitsToDouble(value);
    }
}
