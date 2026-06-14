package io.github.ningpp.compat;

import java.math.BigInteger;

/** Replacement for System.UInt64 (ulong) — Java has no unsigned long. */
public final class ULong {
    private static final BigInteger MAX_VALUE_BI = new BigInteger("18446744073709551615");

    private ULong() {}

    public static BigInteger toBigInteger(long value) {
        if (value >= 0) return BigInteger.valueOf(value);
        return BigInteger.valueOf(value & 0x7FFFFFFFFFFFFFFFL).setBit(63);
    }

    public static String toString(long value) {
        if (value >= 0) return Long.toString(value);
        return toBigInteger(value).toString();
    }

    public static boolean tryParse(String s, LongHolder result) {
        try {
            BigInteger bi = new BigInteger(s.trim());
            if (bi.signum() < 0 || bi.compareTo(MAX_VALUE_BI) > 0) return false;
            result.value = bi.longValue();
            return true;
        } catch (NumberFormatException e) {
            return false;
        }
    }
}
