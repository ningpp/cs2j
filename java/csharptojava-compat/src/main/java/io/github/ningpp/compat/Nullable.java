package io.github.ningpp.compat;

/**
 * Helpers for C# {@code Nullable<T>.Value} / unwrap conversions.
 * Accessing {@code .Value} on a null instance throws
 * {@code InvalidOperationException} in C# (mapped to
 * {@link IllegalStateException}); plain Java unboxing would instead raise a
 * {@link NullPointerException}.
 */
public final class Nullable {
    private Nullable() {
    }

    private static IllegalStateException missingValue() {
        return new IllegalStateException("Nullable object must have a value.");
    }

    public static boolean value(Boolean v) {
        if (v == null) {
            throw missingValue();
        }
        return v;
    }

    public static int value(Integer v) {
        if (v == null) {
            throw missingValue();
        }
        return v;
    }

    public static long value(Long v) {
        if (v == null) {
            throw missingValue();
        }
        return v;
    }

    public static short value(Short v) {
        if (v == null) {
            throw missingValue();
        }
        return v;
    }

    public static byte value(Byte v) {
        if (v == null) {
            throw missingValue();
        }
        return v;
    }

    public static char value(Character v) {
        if (v == null) {
            throw missingValue();
        }
        return v;
    }

    public static float value(Float v) {
        if (v == null) {
            throw missingValue();
        }
        return v;
    }

    public static double value(Double v) {
        if (v == null) {
            throw missingValue();
        }
        return v;
    }
}
