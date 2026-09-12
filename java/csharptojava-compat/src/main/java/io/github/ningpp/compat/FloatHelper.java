package io.github.ningpp.compat;

/**
 * Formats floats the way .NET's invariant-culture float.ToString() produces: shortest round-trip,
 * trailing ".0" trimmed, uppercase E in scientific notation.
 *
 * <p>Going through {@code Double.toString((double) value)} is WRONG: a float widened to double
 * carries the float's exact binary value, whose shortest double representation is much longer than
 * the float's own. Measured: {@code 0.1f} became {@code 0.10000000149011612} where .NET writes
 * {@code 0.1}.</p>
 */
public final class FloatHelper {

    private FloatHelper() {
    }

    public static String toString(float value) {
        if (Float.isNaN(value)) {
            return "NaN";
        }
        if (value == Float.POSITIVE_INFINITY) {
            return "Infinity";
        }
        if (value == Float.NEGATIVE_INFINITY) {
            return "-Infinity";
        }

        // Float.toString already yields the shortest round-trip digits for the FLOAT; only the
        // ".0" suffix and the exponent spelling need to become .NET's.
        String s = Float.toString(value);
        if (s.endsWith(".0")) {
            return s.substring(0, s.length() - 2);
        }
        int ePos = s.indexOf('E');
        if (ePos < 0) {
            ePos = s.indexOf('e');
        }
        if (ePos >= 0) {
            s = s.replace('e', 'E');
            ePos = s.indexOf('E');
            if (ePos > 0 && s.substring(0, ePos).endsWith(".0")) {
                s = s.substring(0, ePos - 2) + s.substring(ePos);
            }
            int expStart = ePos + 1;
            int signLen = (expStart < s.length() && (s.charAt(expStart) == '+' || s.charAt(expStart) == '-')) ? 1 : 0;
            int digitsStart = expStart + signLen;
            int digitsEnd = s.length();
            int digitCount = digitsEnd - digitsStart;
            if (digitCount < 2 && digitCount > 0) {
                StringBuilder sb = new StringBuilder(s.substring(0, digitsStart));
                for (int i = digitCount; i < 2; i++) {
                    sb.append('0');
                }
                sb.append(s.substring(digitsStart));
                s = sb.toString();
            }
        }
        return s;
    }
}
