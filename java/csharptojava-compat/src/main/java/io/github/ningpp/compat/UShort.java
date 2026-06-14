package io.github.ningpp.compat;

/** Replacement for System.UInt16 (ushort) — Java has no unsigned short. */
public final class UShort {
    public static final short MAX_VALUE = (short) 0xFFFF;

    private UShort() {}

    public static int toInt(short value) {
        return value & 0xFFFF;
    }

    public static short fromInt(int value) {
        if (value < 0 || value > 0xFFFF) {
            throw new IllegalArgumentException("Value out of range for ushort: " + value);
        }
        return (short) value;
    }

    public static String toString(short value) {
        return Integer.toString(value & 0xFFFF);
    }

    public static boolean tryParse(String s, ShortHolder result) {
        try {
            int v = Integer.parseInt(s.trim());
            if (v < 0 || v > 0xFFFF) return false;
            result.value = (short) v;
            return true;
        } catch (NumberFormatException e) {
            return false;
        }
    }
}
