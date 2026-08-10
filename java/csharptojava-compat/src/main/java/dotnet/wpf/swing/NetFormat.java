package dotnet.wpf.swing;

/**
 * Helpers to format doubles the way .NET's invariant-culture double.ToString()
 * produces (shortest round-trip, with trailing ".0" trimmed, uppercase E for
 * scientific notation) so compat ToString output matches WPF.
 */
public final class NetFormat {

    private NetFormat() {
    }

    /**
     * Formats a double like .NET invariant double.ToString():
     * 3.5 -> "3.5", 0.0 -> "0", -2.0 -> "-2", 1e-17 -> "1E-17".
     */
    public static String format(double value) {
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
        // Java always emits ".0" for integral doubles; .NET trims it.
        if (s.endsWith(".0")) {
            return s.substring(0, s.length() - 2);
        }
        // Java uses lowercase 'e' in scientific notation; .NET uses uppercase 'E'.
        if (s.indexOf('e') >= 0) {
            s = s.replace('e', 'E');
            // Java writes "1.0E-17"; .NET writes "1E-17".
            int ePos = s.indexOf('E');
            if (ePos > 0 && s.substring(0, ePos).endsWith(".0")) {
                s = s.substring(0, ePos - 2) + s.substring(ePos);
            }
        }
        return s;
    }

    public static String formatPoint(double x, double y) {
        return format(x) + "," + format(y);
    }
}
