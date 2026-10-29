package io.github.ningpp.compat;

import java.util.Base64;

/**
 * Minimal compat stub for System.Convert.
 * Supports the API surface used by Base64Encoder.
 */
public class Convert {

    private Convert() {
    }

    /** Mirrors C# Convert.ToBase64CharArray(byte[], int, int, char[], int) */
    public static int toBase64CharArray(byte[] inArray, int offsetIn, int length, char[] outArray, int offsetOut) {
        byte[] encoded = Base64.getEncoder().encode(java.util.Arrays.copyOfRange(inArray, offsetIn, offsetIn + length));
        for (int i = 0; i < encoded.length && (offsetOut + i) < outArray.length; i++) {
            outArray[offsetOut + i] = (char) encoded[i];
        }
        return Math.min(encoded.length, outArray.length - offsetOut);
    }

    /** Mirrors C# Convert.ToBase64String(byte[], int, int) */
    public static String toBase64String(byte[] inArray, int offset, int length) {
        return Base64.getEncoder().encodeToString(
            java.util.Arrays.copyOfRange(inArray, offset, offset + length));
    }

    /** Mirrors C# Convert.ToBase64String(byte[]) */
    public static String toBase64String(byte[] inArray) {
        return Base64.getEncoder().encodeToString(inArray);
    }

    /** Mirrors C# Convert.FromBase64String(String) — invalid input throws FormatException in .NET. */
    public static byte[] fromBase64String(String s) {
        // .NET requires padding: length%4 == 2 needs "==", length%4 == 3 needs "=",
        // length%4 == 1 is always invalid. Java's decoder accepts unpadded input
        // (e.g. "ABC"/"AB"), which .NET rejects with FormatException.
        int len = s.length();
        if (len % 4 == 1) {
            throw new FormatException("The input is not a valid Base-64 string as it contains a non-base 64 character, more than two padding characters, or an illegal character among the padding characters.");
        }
        if (len % 4 == 2) {
            if (s.charAt(len - 2) != '=' || s.charAt(len - 1) != '=') {
                throw new FormatException("The input is not a valid Base-64 string as it contains a non-base 64 character, more than two padding characters, or an illegal character among the padding characters.");
            }
        } else if (len % 4 == 3) {
            if (s.charAt(len - 1) != '=') {
                throw new FormatException("The input is not a valid Base-64 string as it contains a non-base 64 character, more than two padding characters, or an illegal character among the padding characters.");
            }
        }
        try {
            return Base64.getDecoder().decode(s);
        } catch (IllegalArgumentException e) {
            throw new FormatException(e.getMessage());
        }
    }

    /** Mirrors C# Convert.ToInt32(String) */
    public static char toChar(Object value) {
        if (value == null) {
            return '\0';
        }
        if (value instanceof Character) {
            return (Character) value;
        }
        if (value instanceof Number) {
            return (char) ((Number) value).intValue();
        }
        String s = value.toString();
        return s.length() > 0 ? s.charAt(0) : '\0';
    }

    /**
     * C# Convert.ToByte(string): parses the decimal text and range-checks 0..255
     * (FormatException/OverflowException in C#; NumberFormatException here).
     */
    public static int toByte(String value) {
        int parsed = java.lang.Integer.parseInt(value.trim());
        if (parsed < 0 || parsed > 255) {
            throw new java.lang.NumberFormatException("Value was either too large or too small for an unsigned byte.");
        }
        return parsed;
    }

    /** C# Convert.ToInt32(char): the code point of the character. */
    public static int toInt32(char value) {
        return value;
    }

    public static int toInt32(String value) {        return java.lang.Integer.parseInt(value);
    }

    /**
     * Mirrors C# Convert.ToInt64(Object). null → 0; Boolean → 1/0; numeric →
     * longValue; Character → code point; String → parse with FormatException
     * re-wrap (translated catches). Anything else is the ClassCastException
     * analogue of C# InvalidCastException.
     */
    public static long toInt64(Object value) {
        if (value == null) {
            return 0L;
        }
        if (value instanceof Long) {
            return (Long) value;
        }
        if (value instanceof Number) {
            return ((Number) value).longValue();
        }
        if (value instanceof Boolean) {
            return (Boolean) value ? 1L : 0L;
        }
        if (value instanceof Character) {
            return ((Character) value).charValue();
        }
        if (value instanceof String) {
            try {
                return Long.parseLong(((String) value).trim());
            } catch (NumberFormatException e) {
                throw new FormatException(e.getMessage(), e);
            }
        }
        throw new ClassCastException(
                "Cannot convert " + value.getClass().getName() + " to long");
    }

    /**
     * Mirrors C# Convert.ToInt32(String, IFormatProvider): int.Parse with
     * NumberStyles.Integer (whitespace + sign, no thousands). Bad input throws
     * FormatException so translated catches (FormatException) keep working.
     */
    public static int toInt32(String value, CultureInfo provider) {
        return parseIntWithProvider(value);
    }

    /** Mirrors C# Convert.ToInt32(String, IFormatProvider) — NumberFormatInfo provider. */
    public static int toInt32(String value, NumberFormatInfo provider) {
        return parseIntWithProvider(value);
    }

    /**
     * Mirrors C# Convert.ToInt32(char, IFormatProvider): the code point of the
     * character; the provider is ignored by .NET for this overload.
     */
    public static int toInt32(char value, CultureInfo provider) {
        return value;
    }

    private static int parseIntWithProvider(String value) {
        try {
            return java.lang.Integer.parseInt(value.trim());
        } catch (NumberFormatException e) {
            throw new FormatException(e.getMessage(), e);
        }
    }

    /** Mirrors C# Convert.ToBoolean(String): Boolean.Parse (trim + True/False, case-insensitive). */
    public static boolean toBoolean(String value) {
        return parseBooleanWithProvider(value);
    }

    public static boolean toBoolean(String value, CultureInfo provider) {
        return parseBooleanWithProvider(value);
    }

    /** Mirrors C# Convert.ToBoolean(String, IFormatProvider) — NumberFormatInfo provider. */
    public static boolean toBoolean(String value, NumberFormatInfo provider) {
        return parseBooleanWithProvider(value);
    }

    private static boolean parseBooleanWithProvider(String value) {
        String trimmed = value.trim();
        if (trimmed.equalsIgnoreCase("true")) {
            return true;
        }
        if (trimmed.equalsIgnoreCase("false")) {
            return false;
        }
        throw new FormatException("String '" + value + "' was not recognized as a valid Boolean.");
    }

    /**
     * Mirrors C# Convert.ToBoolean(Object, IFormatProvider): null → false;
     * Boolean → itself; String → Boolean.Parse; numeric → 0 is false (matches
     * .NET Core 3.0+ IConvertible.ToBoolean); anything else is the
     * ClassCastException analogue of C# InvalidCastException.
     */
    public static boolean toBoolean(Object value, CultureInfo provider) {
        return toBooleanObject(value);
    }

    /** Mirrors C# Convert.ToBoolean(Object, IFormatProvider) — NumberFormatInfo provider. */
    public static boolean toBoolean(Object value, NumberFormatInfo provider) {
        return toBooleanObject(value);
    }

    private static boolean toBooleanObject(Object value) {
        if (value == null) {
            return false;
        }
        if (value instanceof Boolean) {
            return (Boolean) value;
        }
        if (value instanceof String) {
            return parseBooleanWithProvider((String) value);
        }
        if (value instanceof Number) {
            return ((Number) value).doubleValue() != 0.0;
        }
        if (value instanceof Character) {
            throw new ClassCastException(
                    "Cannot convert char to Boolean (C# InvalidCastException analogue)");
        }
        throw new ClassCastException(
                "Cannot convert " + value.getClass().getName() + " to boolean");
    }

/** Mirrors C# Convert.ToDecimal(Object, IFormatProvider) */
public static Decimal toDecimal(Object value, NumberFormatInfo info) {
return toDecimal(value);
}

/** Mirrors C# Convert.ToDecimal(Object, IFormatProvider) with CultureInfo */
public static Decimal toDecimal(Object value, CultureInfo info) {
return toDecimal(value);
}

    /** Mirrors C# Convert.ToDecimal(Object) */
    public static Decimal toDecimal(Object value) {
        return Decimal.valueOf(value);
    }

    /**
     * Mirrors C# Convert.ToDouble(Object, IFormatProvider). null → 0d;
     * Boolean → 1/0; numeric → value; Character → code point; String → parse
     * (FormatException surfaces like C#). Anything else is the ClassCastException
     * analogue of C# InvalidCastException (compat convention, see CSharpArray).
     */
    public static double toDouble(Object value) {
        if (value == null) {
            return 0.0;
        }
        if (value instanceof Double) {
            return (Double) value;
        }
        if (value instanceof Number) {
            return ((Number) value).doubleValue();
        }
        if (value instanceof Boolean) {
            return (Boolean) value ? 1.0 : 0.0;
        }
        if (value instanceof Character) {
            return ((Character) value).charValue();
        }
        if (value instanceof String) {
            // C# Convert.ToDouble(String) throws FormatException on bad input;
            // NumberFormatException is not one, so re-wrap for translated catches.
            try {
                return Double.parseDouble(((String) value).trim());
            } catch (NumberFormatException e) {
                throw new FormatException(e.getMessage(), e);
            }
        }
        throw new ClassCastException(
                "Cannot convert " + value.getClass().getName() + " to double");
    }

    /** Mirrors C# Convert.ToDouble(Object, IFormatProvider) — invariant provider. */
    public static double toDouble(Object value, NumberFormatInfo info) {
        return toDouble(value);
    }

    /** Mirrors C# Convert.ToDouble(Object, IFormatProvider) with CultureInfo. */
    public static double toDouble(Object value, CultureInfo info) {
        return toDouble(value);
    }

    /**
     * Mirrors C# Convert.ToString(Object, IFormatProvider): null → empty string;
     * String → itself; boxed primitives/other → toString.
     * Double/Float render per .NET's default (shortest round-trip) shape — Java's
     * Double.toString yields "1.0" where C# ToString() yields "1" (XPath
     * concat("… = ", position()) produced "= 1.0").
     */
    /** Mirrors C# Convert.ToString(int value, int toBase) (bases 2/8/10/16). */
    public static String toString(int value, int toBase) {
        return Integer.toString(value, toBase);
    }

    public static String toString(Object value) {
        if (value == null) return "";
        if (value instanceof Double || value instanceof Float) {
            return MathHelper.formatRoundTrip(value);
        }
        return value.toString();
    }

    /** Mirrors C# Convert.ToString(Object, IFormatProvider) — invariant provider. */
    public static String toString(Object value, NumberFormatInfo info) {
        return toString(value);
    }

    /** Mirrors C# Convert.ToString(Object, IFormatProvider) with CultureInfo. */
    public static String toString(Object value, CultureInfo info) {
        return toString(value);
    }

    /** Mirrors C# Convert.ChangeType(Object, Type) */
    public static Object changeType(Object value, Class<?> targetType) {
        return changeType(value, targetType, null);
    }

    /** Mirrors C# Convert.ChangeType(Object, Type, IFormatProvider) for common types */
    public static Object changeType(Object value, Class<?> targetType, IFormatProvider provider) {
        if (value == null) {
            if (targetType == String.class) return null;
            if (targetType.isPrimitive()) throw new IllegalArgumentException("Cannot convert null to primitive type");
            return null;
        }
        if (targetType.isInstance(value)) {
            return value;
        }
        if (value instanceof String) {
            return changeType((String) value, targetType, provider);
        }
        if (targetType == String.class) return value.toString();
        if ((targetType == java.lang.Integer.class || targetType == int.class) && value instanceof Number) return ((Number) value).intValue();
        if ((targetType == Long.class || targetType == long.class) && value instanceof Number) return ((Number) value).longValue();
        if ((targetType == Double.class || targetType == double.class) && value instanceof Number) return ((Number) value).doubleValue();
        if ((targetType == Float.class || targetType == float.class) && value instanceof Number) return ((Number) value).floatValue();
        if ((targetType == Short.class || targetType == short.class) && value instanceof Number) return ((Number) value).shortValue();
        if ((targetType == Byte.class || targetType == byte.class) && value instanceof Number) return ((Number) value).byteValue();
        if (targetType == java.math.BigDecimal.class && value instanceof Number) return java.math.BigDecimal.valueOf(((Number) value).doubleValue());
        return value;
    }

    private static Object changeType(String value, Class<?> targetType, IFormatProvider provider) {
        if (value == null) {
            if (targetType == String.class) return null;
            if (targetType.isPrimitive()) throw new IllegalArgumentException("Cannot convert null to primitive type");
            return null;
        }
        if (targetType == String.class) return value;
        if (targetType == java.lang.Integer.class || targetType == int.class) return java.lang.Integer.parseInt(value.trim());
        if (targetType == Long.class || targetType == long.class) return Long.parseLong(value.trim());
        if (targetType == Double.class || targetType == double.class) return Double.parseDouble(value.trim());
        if (targetType == Float.class || targetType == float.class) return Float.parseFloat(value.trim());
        if (targetType == Boolean.class || targetType == boolean.class) return Boolean.parseBoolean(value.trim());
        if (targetType == Short.class || targetType == short.class) return Short.parseShort(value.trim());
        if (targetType == Byte.class || targetType == byte.class) return Byte.parseByte(value.trim());
        if (targetType == Character.class || targetType == char.class) {
            if (value.length() > 0) return value.charAt(0);
            return '\0';
        }
        if (targetType == java.math.BigDecimal.class) return new java.math.BigDecimal(value.trim());
        return value;
    }
}
