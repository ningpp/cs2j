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

        // Java's Double.toString and .NET's shortest round-trip agree on the digits,
        // but not on the notation: Java switches to scientific at 1e7 / 1e-3 and
        // prints "E9" without a sign, while .NET keeps fixed notation for decimal
        // exponents in [-4, 14] and prints "E+15" / "E-05" (sign always, at least
        // two exponent digits). Reformat Java's digits into .NET's layout, e.g.
        // "1.97009799E9" -> "1970097990".
        String s = Double.toString(value);
        boolean negative = s.startsWith("-");
        if (negative) {
            s = s.substring(1);
        }

        long javaExponent = 0;
        int ePos = s.indexOf('E');
        if (ePos < 0) {
            ePos = s.indexOf('e');
        }
        if (ePos >= 0) {
            javaExponent = Long.parseLong(s.substring(ePos + 1));
            s = s.substring(0, ePos);
        }

        int dotPos = s.indexOf('.');
        if (dotPos < 0) {
            dotPos = s.length();
        }
        String digits = s.replace(".", "");
        int leadingZeros = 0;
        while (leadingZeros < digits.length() && digits.charAt(leadingZeros) == '0') {
            leadingZeros++;
        }
        // value == digits x 10^(e10 - digits.length() + 1)
        int e10 = (int) javaExponent + dotPos - 1 - leadingZeros;

        // Only significant digits remain: strip leading and trailing zeros.
        int end = digits.length();
        while (end > 0 && digits.charAt(end - 1) == '0') {
            end--;
        }
        digits = digits.substring(leadingZeros, Math.max(end, leadingZeros));
        if (digits.isEmpty()) {
            return "0"; // also covers -0.0, which .NET prints as "0"
        }

        StringBuilder result = new StringBuilder();
        if (negative) {
            result.append('-');
        }
        if (e10 >= -4 && e10 <= 14) {
            // Fixed notation: e10 + 1 digits stay left of the decimal point.
            int intDigits = e10 + 1;
            if (intDigits <= 0) {
                result.append("0.");
                for (int i = 0; i < -intDigits; i++) {
                    result.append('0');
                }
                result.append(digits);
            } else if (intDigits >= digits.length()) {
                result.append(digits);
                for (int i = digits.length(); i < intDigits; i++) {
                    result.append('0');
                }
            } else {
                result.append(digits, 0, intDigits).append('.').append(digits, intDigits, digits.length());
            }
        } else {
            // Scientific notation: "d[.ddd]E[+/-]NN" with at least two exponent digits.
            result.append(digits.charAt(0));
            if (digits.length() > 1) {
                result.append('.').append(digits, 1, digits.length());
            }
            result.append('E');
            int magnitude = Math.abs(e10);
            result.append(e10 < 0 ? '-' : '+');
            if (magnitude < 10) {
                result.append('0');
            }
            result.append(magnitude);
        }
        return result.toString();
    }
}
