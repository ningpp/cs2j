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
        if (s.indexOf('e') >= 0) {
            s = s.replace('e', 'E');
            int ePos = s.indexOf('E');
            if (ePos > 0 && s.substring(0, ePos).endsWith(".0")) {
                s = s.substring(0, ePos - 2) + s.substring(ePos);
            }
        }
        return s;
    }
}
