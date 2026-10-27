package io.github.ningpp.compat;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.util.Locale;
import java.util.Objects;

public final class Decimal implements Comparable<Decimal> {
    public static final int MAX_SCALE = 28;

    private static final BigInteger MAX_MANTISSA =
        new BigInteger("79228162514264337593543950335");
    private static final BigDecimal MAX_BIG_DECIMAL = new BigDecimal(MAX_MANTISSA);
    private static final BigDecimal MIN_BIG_DECIMAL = MAX_BIG_DECIMAL.negate();

    /**
     * decimal.MaxValue has 29 integer digits; any value with more integer digits
     * than that necessarily overflows, whatever its scale is.
     */
    private static final int MAX_INTEGER_DIGITS = 29;

    public static final Decimal ZERO = new Decimal(BigDecimal.ZERO, false);
    public static final Decimal ONE = new Decimal(BigDecimal.ONE, false);
    public static final Decimal MINUS_ONE = new Decimal(BigDecimal.ONE.negate(), false);
    public static final Decimal MAX_VALUE = new Decimal(MAX_BIG_DECIMAL, false);
    public static final Decimal MIN_VALUE = new Decimal(MIN_BIG_DECIMAL, false);

    /**
     * Allocates a 1D decimal array whose elements default to 0m, matching C#
     * {@code new decimal[n]} (Java reference arrays start as null).
     */
    public static Decimal[] array(int length) {
        Decimal[] result = new Decimal[length];
        java.util.Arrays.fill(result, ZERO);
        return result;
    }

    private final BigDecimal value;

    public Decimal(int value) {
        this(BigDecimal.valueOf(value), true);
    }

    public Decimal(long value) {
        this(BigDecimal.valueOf(value), true);
    }

    /**
     * Creates a Decimal from a long that represents an unsigned 64-bit value.
     * C# ulong is mapped to Java long; values with the high bit set represent
     * numbers greater than Long.MAX_VALUE.
     */
    public static Decimal createFrom_long(long value) {
        return new Decimal(new BigDecimal(Long.toUnsignedString(value)), true);
    }

    public Decimal(double value) {
        this(fromDouble(value), true);
    }

    public Decimal(BigDecimal value) {
        this(value, true);
    }

    /**
     * Mirrors C# Decimal(int low, int mid, int high, bool isNegative, byte scale).
     * Builds a 96-bit signed integer from the three 32-bit words, then applies
     * the specified scale (number of decimal places).
     */
    public Decimal(int low, int mid, int high, boolean isNegative, int scale) {
        this(buildFromParts(low, mid, high, isNegative, scale), true);
    }

    private static BigDecimal buildFromParts(int low, int mid, int high, boolean isNegative, int scale) {
        if (scale < 0 || scale > MAX_SCALE) {
            throw new IllegalArgumentException("scale must be between 0 and " + MAX_SCALE);
        }
        long unsignedLow = low & 0xFFFFFFFFL;
        long unsignedMid = mid & 0xFFFFFFFFL;
        long unsignedHigh = high & 0xFFFFFFFFL;
        BigInteger mantissa = BigInteger.valueOf(unsignedHigh)
            .shiftLeft(64)
            .or(BigInteger.valueOf(unsignedMid).shiftLeft(32))
            .or(BigInteger.valueOf(unsignedLow));
        if (isNegative) {
            mantissa = mantissa.negate();
        }
        return new BigDecimal(mantissa, scale);
    }

    private Decimal(BigDecimal value, boolean validate) {
        if (value == null) {
            throw new NullPointerException("value");
        }

        if (validate) {
            checkIntegerDigits(value);
        }

        BigDecimal normalized = canonicalize(value);
        if (validate) {
            validateRange(normalized);
        }
        this.value = normalized;
    }

    public static Decimal valueOf(int value) {
        return new Decimal(value);
    }

    public static Decimal valueOf(long value) {
        return new Decimal(value);
    }

    public static Decimal valueOf(double value) {
        return new Decimal(value);
    }

    public static Decimal valueOf(BigDecimal value) {
        return new Decimal(value);
    }

    /// <summary>
    /// Mimics C# Convert.ToDecimal(object):
    ///   - null        → ZERO
    ///   - bool        → ONE / ZERO
    ///   - char        → Unicode code point as decimal
    ///   - numeric     → implicit widening
    ///   - String      → parse (null-safe: returns ZERO for null)
    ///   - BigInteger/BigDecimal → via BigDecimal
    /// </summary>
    public static Decimal valueOf(Object value) {
        if (value == null) {
            // C#: Convert.ToDecimal(null) returns 0m
            return ZERO;
        }
        if (value instanceof Decimal d) {
            return d;
        }
        if (value instanceof Integer i) {
            return new Decimal(i.intValue());
        }
        if (value instanceof Long l) {
            return new Decimal(l.longValue());
        }
        if (value instanceof Double d) {
            return new Decimal(d.doubleValue());
        }
        if (value instanceof Float f) {
            return new Decimal(f.doubleValue());
        }
        if (value instanceof Short s) {
            return new Decimal(s.intValue());
        }
        if (value instanceof Byte b) {
            return new Decimal(b.intValue());
        }
        if (value instanceof Character c) {
            // C#: Convert.ToDecimal(char) returns the Unicode code point as decimal
            return new Decimal(c.charValue());
        }
        if (value instanceof Boolean b) {
            // C#: Convert.ToDecimal(true) → 1m, Convert.ToDecimal(false) → 0m
            return b.booleanValue() ? ONE : ZERO;
        }
        if (value instanceof BigInteger bi) {
            return new Decimal(new BigDecimal(bi));
        }
        if (value instanceof BigDecimal bd) {
            return new Decimal(bd);
        }
        if (value instanceof String str) {
            if (str.isEmpty()) {
                return ZERO;
            }
            return parse(str);
        }
        throw new IllegalArgumentException(
            "Cannot convert " + value.getClass().getName() + " to Decimal.");
    }

    public static Decimal parse(String value) {
        return parseCore(value, NumberStyles.Number);
    }

    public static Decimal parse(String value, CultureInfo culture) {
        return parse(value);
    }

    public static Decimal parse(String value, NumberFormatInfo formatInfo) {
        return parse(value);
    }

    public static Decimal parse(String value, java.util.Locale locale) {
        return parse(value);
    }

    public static Decimal parse(String value, int style, CultureInfo culture) {
        return parseCore(value, style);
    }

    public static Decimal parse(String value, int style, NumberFormatInfo formatInfo) {
        return parseCore(value, style);
    }

    public static Decimal parse(String value, int style, java.util.Locale locale) {
        return parseCore(value, style);
    }

    public static boolean tryParse(String value, ObjectHolder<Decimal> result) {
        Objects.requireNonNull(result, "result");
        try {
            result.value = parse(value);
            return true;
        } catch (RuntimeException ex) {
            result.value = ZERO;
            return false;
        }
    }

    public static boolean tryParse(String value, int style, CultureInfo culture, ObjectHolder<Decimal> result) {
        return tryParseWithStyle(value, style, result);
    }

    public static boolean tryParse(String value, int style, NumberFormatInfo formatInfo, ObjectHolder<Decimal> result) {
        return tryParseWithStyle(value, style, result);
    }

    public static boolean tryParse(String value, int style, java.util.Locale locale, ObjectHolder<Decimal> result) {
        return tryParseWithStyle(value, style, result);
    }

    private static boolean tryParseWithStyle(String value, int style, ObjectHolder<Decimal> result) {
        Objects.requireNonNull(result, "result");
        try {
            result.value = new Decimal(new BigDecimal(normalizeNumberString(value, style)), true);
            return true;
        } catch (RuntimeException ex) {
            result.value = ZERO;
            return false;
        }
    }

    private static Decimal parseCore(String value, int style) {
        try {
            String normalized = normalizeNumberString(value, style);
            return new Decimal(new BigDecimal(normalized), true);
        } catch (NumberFormatException ex) {
            throw new FormatException(ex.getMessage(), ex);
        }
    }

    /**
     * Parses exponent notation such as "1e-10" with full decimal range
     * validation. .NET's Utf8Parser.TryParseDecimal accepts exponents for
     * JSON numbers although decimal.Parse (NumberStyles.Number) does not, so
     * this is the entry point used by Utf8Parser only.
     */
    public static Decimal parseExponent(String value) {
        if (value == null) {
            throw new FormatException("null");
        }
        try {
            return new Decimal(new BigDecimal(value.trim()), true);
        } catch (NumberFormatException ex) {
            throw new FormatException(ex.getMessage(), ex);
        }
    }

    public Decimal add(Decimal other) {
        return checked(value.add(require(other).value));
    }

    public static Decimal add(Decimal left, Decimal right) {
        return require(left).add(right);
    }

    public Decimal subtract(Decimal other) {
        return checked(value.subtract(require(other).value));
    }

    public static Decimal subtract(Decimal left, Decimal right) {
        return require(left).subtract(right);
    }

    public Decimal multiply(Decimal other) {
        return checked(value.multiply(require(other).value));
    }

    public static Decimal multiply(Decimal left, Decimal right) {
        return require(left).multiply(right);
    }

    public Decimal divide(Decimal other) {
        Decimal divisor = require(other);
        if (divisor.value.signum() == 0) {
            throw new ArithmeticException("Division by zero");
        }

        try {
            BigDecimal exact = value.divide(divisor.value);
            if (exact.scale() <= MAX_SCALE) {
                return checked(exact);
            }
            // Exact quotient needs more than 28 decimal places: clamp with
            // bankers rounding like C#. A result that rounds to zero reports
            // scale 0 in C# (1m/2e28m prints "0"), unlike BigDecimal zeros.
            BigDecimal rounded = exact.setScale(MAX_SCALE, RoundingMode.HALF_EVEN);
            if (rounded.signum() == 0) {
                rounded = rounded.setScale(0);
            }
            return checked(rounded);
        } catch (ArithmeticException ex) {
            // Non-terminating exact quotient: round to 28 digits, ties to even.
            BigDecimal divided = value.divide(divisor.value, MAX_SCALE, RoundingMode.HALF_EVEN);
            if (divided.signum() == 0 && divided.scale() > 0) {
                divided = divided.setScale(0);
            }
            return checked(divided);
        }
    }

    public static Decimal divide(Decimal left, Decimal right) {
        return require(left).divide(right);
    }

    public Decimal remainder(Decimal other) {
        Decimal divisor = require(other);
        if (divisor.value.signum() == 0) {
            throw new ArithmeticException("Division by zero");
        }
        return checked(value.remainder(divisor.value));
    }

    public static Decimal remainder(Decimal left, Decimal right) {
        return require(left).remainder(right);
    }

    public Decimal negate() {
        return checked(value.negate());
    }

    public static Decimal negate(Decimal value) {
        return require(value).negate();
    }

    public Decimal abs() {
        return value.signum() < 0 ? negate() : this;
    }

    public static Decimal abs(Decimal value) {
        return require(value).abs();
    }

    public Decimal round() {
        return round(0);
    }

    public Decimal round(int decimals) {
        return round(decimals, RoundingMode.HALF_EVEN);
    }

    public Decimal round(int decimals, RoundingMode mode) {
        validateScaleArgument(decimals);
        return checked(value.setScale(decimals, Objects.requireNonNull(mode, "mode")));
    }

    public static Decimal round(Decimal value) {
        return require(value).round();
    }

    public static Decimal round(Decimal value, int decimals) {
        return require(value).round(decimals);
    }

    public static Decimal round(Decimal value, int decimals, RoundingMode mode) {
        return require(value).round(decimals, mode);
    }

    public Decimal truncate() {
        return checked(value.setScale(0, RoundingMode.DOWN));
    }

    public static Decimal truncate(Decimal value) {
        return require(value).truncate();
    }

    public Decimal floor() {
        return checked(value.setScale(0, RoundingMode.FLOOR));
    }

    public static Decimal floor(Decimal value) {
        return require(value).floor();
    }

    public Decimal ceiling() {
        return checked(value.setScale(0, RoundingMode.CEILING));
    }

    public static Decimal ceiling(Decimal value) {
        return require(value).ceiling();
    }

    public BigDecimal toBigDecimal() {
        return value;
    }

    public int intValue() {
        return value.setScale(0, RoundingMode.DOWN).intValueExact();
    }

    public long longValue() {
        return value.setScale(0, RoundingMode.DOWN).longValueExact();
    }

    public double doubleValue() {
        return value.doubleValue();
    }

    public float floatValue() {
        return value.floatValue();
    }

    public String toStringPlain() {
        // C# decimal.ToString strips trailing zeros (e.g. 0.000050 → "0.00005"),
        // but BigDecimal.toPlainString() preserves them. Use stripTrailingZeros
        // to match C# behavior (real chain: XmlConvert.ToString(decimal)).
        return value.stripTrailingZeros().toPlainString();
    }

    @Override
    public String toString() {
        return toStringPlain();
    }

    public String toString(String format, IFormatProvider provider) {
        if (format == null || format.isEmpty()) {
            return toStringPlain();
        }
        return MathHelper.formatNumeric(format, doubleValue());
    }

    public String toString(IFormatProvider provider) {
        return toStringPlain();
    }

    @Override
    public int compareTo(Decimal other) {
        return value.compareTo(require(other).value);
    }

    /**
     * Static comparison surface used by the C34 decimal-ops normalization
     * ({@code decimal.Compare(a, b)} in C# maps here).
     */
    public static int compare(Decimal left, Decimal right) {
        return require(left).compareTo(right);
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof Decimal decimal && compareTo(decimal) == 0;
    }

    @Override
    public int hashCode() {
        return canonicalForEquality(value).hashCode();
    }

    private static Decimal checked(BigDecimal value) {
        return new Decimal(value, true);
    }

    private static Decimal require(Decimal value) {
        return Objects.requireNonNull(value, "value");
    }

    private static BigDecimal fromDouble(double value) {
        if (!Double.isFinite(value)) {
            throw new NumberFormatException("Value was either too large or too small for a Decimal.");
        }
        return BigDecimal.valueOf(value);
    }

    private static String normalizeNumberString(String value, int style) {
        if (value == null) {
            throw new NumberFormatException("null");
        }

        String normalized = value;
        if ((style & NumberStyles.AllowLeadingWhite) != 0) {
            normalized = stripLeading(normalized);
        }
        if ((style & NumberStyles.AllowTrailingWhite) != 0) {
            normalized = stripTrailing(normalized);
        }
        if (normalized.isEmpty()) {
            throw new NumberFormatException("empty");
        }

        boolean negative = false;
        if ((style & NumberStyles.AllowParentheses) != 0
            && normalized.length() >= 2
            && normalized.charAt(0) == '('
            && normalized.charAt(normalized.length() - 1) == ')') {
            negative = true;
            normalized = normalized.substring(1, normalized.length() - 1);
        } else if (normalized.indexOf('(') >= 0 || normalized.indexOf(')') >= 0) {
            throw new NumberFormatException(value);
        }

        if ((style & NumberStyles.AllowLeadingSign) != 0
            && (normalized.startsWith("+") || normalized.startsWith("-"))) {
            negative = normalized.charAt(0) == '-';
            normalized = normalized.substring(1);
        }

        if ((style & NumberStyles.AllowTrailingSign) != 0
            && (normalized.endsWith("+") || normalized.endsWith("-"))) {
            if (negative) {
                throw new NumberFormatException(value);
            }
            negative = normalized.charAt(normalized.length() - 1) == '-';
            normalized = normalized.substring(0, normalized.length() - 1);
        }

        if ((style & NumberStyles.AllowThousands) != 0) {
            normalized = normalized.replace(",", "");
        } else if (normalized.indexOf(',') >= 0) {
            throw new NumberFormatException(value);
        }

        if ((style & NumberStyles.AllowDecimalPoint) == 0 && normalized.indexOf('.') >= 0) {
            throw new NumberFormatException(value);
        }

        if ((style & NumberStyles.AllowExponent) == 0
            && (normalized.indexOf('e') >= 0 || normalized.indexOf('E') >= 0)) {
            throw new NumberFormatException(value);
        }

        if (negative) {
            normalized = "-" + normalized;
        }
        return normalized;
    }

    private static String stripLeading(String value) {
        int i = 0;
        while (i < value.length() && Character.isWhitespace(value.charAt(i))) {
            i++;
        }
        return value.substring(i);
    }

    private static String stripTrailing(String value) {
        int i = value.length() - 1;
        while (i >= 0 && Character.isWhitespace(value.charAt(i))) {
            i--;
        }
        return value.substring(0, i + 1);
    }

    /**
     * O(1) overflow pre-check performed before normalization.
     *
     * C# decimal is a 96-bit mantissa plus a scale byte, so the runtime parser
     * rejects an out-of-range exponent arithmetically (Number.TryNumberToDecimal:
     * "if (e &gt; DecimalPrecision) return false;", DecimalPrecision = 29) without
     * ever building the scaled value. The BigDecimal model has to normalize
     * first, and for a negative scale that means materializing 10^(-scale):
     * a JSON number such as "1e+100000002" (see JsonDocumentTests.readTooPreciseDouble)
     * would allocate a 100_000_002 digit BigInteger and burn ~40s only for
     * validateRange() to reject the result afterwards. Anything with more than
     * MAX_INTEGER_DIGITS integer digits overflows regardless of its scale, so it
     * can be rejected up front with the exact exception validateRange() throws.
     */
    private static void checkIntegerDigits(BigDecimal value) {
        if (value.signum() == 0) {
            // Zero never overflows whatever its scale is (C# TryNumberToDecimal
            // returns 0 for an empty digit buffer before looking at the scale),
            // and BigDecimal.setScale() resolves zero without a power of ten.
            return;
        }
        long integerDigits = (long) value.precision() - value.scale();
        if (integerDigits > MAX_INTEGER_DIGITS) {
            throw new ArithmeticException("Decimal overflow.");
        }
    }

    private static BigDecimal canonicalize(BigDecimal value) {
        BigDecimal normalized = value;
        if (normalized.scale() > MAX_SCALE) {
            // |value| < 10^-(MAX_SCALE + 1) is below half of the last
            // representable place, so rounding it to 28 decimals is zero.
            // Taking the long route would build 10^(scale - 28) first:
            // "1e-100000002" needs a 100_000_000 digit power of ten for a
            // result that is known to be zero.
            long integerDigits = (long) normalized.precision() - normalized.scale();
            if (integerDigits <= -(MAX_SCALE + 1)) {
                return new BigDecimal(BigInteger.ZERO, MAX_SCALE);
            }
            normalized = normalized.setScale(MAX_SCALE, RoundingMode.HALF_EVEN);
        }

        if (normalized.scale() < 0) {
            // checkIntegerDigits() guarantees at most MAX_INTEGER_DIGITS integer
            // digits here, so this expansion multiplies by at most 10^28.
            normalized = normalized.setScale(0);
        }
        if (normalized.scale() > MAX_SCALE) {
            throw new ArithmeticException("Decimal scale exceeds 28.");
        }
        return normalized;
    }

    private static BigDecimal canonicalForEquality(BigDecimal value) {
        BigDecimal stripped = value.stripTrailingZeros();
        if (stripped.signum() == 0) {
            return BigDecimal.ZERO;
        }
        return stripped.scale() < 0 ? stripped.setScale(0) : stripped;
    }

    private static void validateRange(BigDecimal value) {
        if (value.compareTo(MAX_BIG_DECIMAL) > 0 || value.compareTo(MIN_BIG_DECIMAL) < 0) {
            throw new ArithmeticException("Decimal overflow.");
        }

        int scale = Math.max(value.scale(), 0);
        BigInteger scaledInteger = value.movePointRight(scale).toBigIntegerExact().abs();
        if (scaledInteger.compareTo(MAX_MANTISSA) > 0) {
            throw new ArithmeticException("Decimal overflow.");
        }
    }

    private static void validateScaleArgument(int decimals) {
        if (decimals < 0 || decimals > MAX_SCALE) {
            throw new IllegalArgumentException("decimals must be between 0 and 28");
        }
    }
}
