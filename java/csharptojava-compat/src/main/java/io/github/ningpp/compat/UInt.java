package io.github.ningpp.compat;

/** Replacement for System.UInt32 (uint) — Java has no unsigned int. */
public final class UInt {
    public static final int MAX_VALUE = 0xFFFFFFFF;

    private UInt() {}

    public static long toLong(int value) {
        return value & 0xFFFFFFFFL;
    }

    public static int fromLong(long value) {
        if (value < 0 || value > 0xFFFFFFFFL) {
            throw new IllegalArgumentException("Value out of range for uint: " + value);
        }
        return (int) value;
    }

    public static String toString(int value) {
        return Long.toString(value & 0xFFFFFFFFL);
    }

    public static boolean tryParse(String s, IntHolder result) {
        try {
            long v = Long.parseLong(s.trim());
            if (v < 0 || v > 0xFFFFFFFFL) return false;
            result.value = (int) v;
            return true;
        } catch (NumberFormatException e) {
            return false;
        }
    }
}
