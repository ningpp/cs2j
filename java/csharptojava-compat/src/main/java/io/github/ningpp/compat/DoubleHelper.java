package io.github.ningpp.compat;

/**
 * Formats doubles the way .NET's invariant-culture double.ToString() produces:
 * shortest round-trip, trailing ".0" trimmed, uppercase E in scientific notation.
 */
public final class DoubleHelper {

    private DoubleHelper() {
    }

    public static String toString(double value) {
        if (Double.isNaN(value)) {
            return "NaN";
        }
        if (value == Double.POSITIVE_INFINITY) {
            return "Infinity";
        }
        if (value == Double.NEGATIVE_INFINITY) {
            return "-Infinity";
        }

        String s = Double.toString(value);
        if (s.endsWith(".0")) {
            return s.substring(0, s.length() - 2);
        }
        // Java's Double.toString prints an uppercase 'E' for exponents
        // (e.g. "1.23456789E-8").
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
            // .NET G-format exponents have at least two digits ("E-08");
            // Java prints "E-8". Pad the exponent digits to two.
            int expStart = ePos + 1;
            int signLen = (expStart < s.length() && (s.charAt(expStart) == '+' || s.charAt(expStart) == '-')) ? 1 : 0;
            int digitsStart = expStart + signLen;
            int digitsEnd = s.length();
            int digitCount = digitsEnd - digitsStart;
            if (digitCount < 2 && digitCount > 0) {
                StringBuilder sb = new StringBuilder(s.substring(0, digitsStart));
                for (int i = digitCount; i < 2; i++) sb.append('0');
                sb.append(s.substring(digitsStart));
                s = sb.toString();
            }
        }
        return s;
    }
}
