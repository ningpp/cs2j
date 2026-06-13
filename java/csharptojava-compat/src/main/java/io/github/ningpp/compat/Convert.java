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

    /** Mirrors C# Convert.FromBase64String(String) */
    public static byte[] fromBase64String(String s) {
        return Base64.getDecoder().decode(s);
    }

    /** Mirrors C# Convert.ToInt32(String) */
    public static int toInt32(String value) {
        return Integer.parseInt(value);
    }

    /** Mirrors C# Convert.ToBoolean(String) */
    public static boolean toBoolean(String value) {
        return Boolean.parseBoolean(value);
    }
}
