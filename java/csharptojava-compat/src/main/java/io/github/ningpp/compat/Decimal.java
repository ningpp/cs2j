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

    public static final Decimal ZERO = new Decimal(BigDecimal.ZERO, false);
    public static final Decimal ONE = new Decimal(BigDecimal.ONE, false);
    public static final Decimal MINUS_ONE = new Decimal(BigDecimal.ONE.negate(), false);
    public static final Decimal MAX_VALUE = new Decimal(MAX_BIG_DECIMAL, false);
    public static final Decimal MIN_VALUE = new Decimal(MIN_BIG_DECIMAL, false);

    private final BigDecimal value;

    public Decimal(int value) {
        this(BigDecimal.valueOf(value), true);
    }

    public Decimal(long value) {
        this(BigDecimal.valueOf(value), true);
    }

    public Decimal(double value) {
        this(fromDouble(value), true);
    }

    public Decimal(BigDecimal value) {
        this(value, true);
    }

    private Decimal(BigDecimal value, boolean validate) {
        if (value == null) {
            throw new NullPointerException("value");
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

    public static Decimal parse(String value) {
        if (value == null) {
            throw new NumberFormatException("null");
        }
        String normalized = normalizeNumberString(value, NumberStyles.Number);
        if (normalized.isEmpty()) {
            throw new NumberFormatException("empty");
        }
        return new Decimal(new BigDecimal(normalized), true);
    }

    public static Decimal parse(String value, CultureInfo culture) {
        return parse(value);
    }

    public static Decimal parse(String value, java.util.Locale locale) {
        return parse(value);
    }

    public static Decimal parse(String value, int style, CultureInfo culture) {
        return new Decimal(new BigDecimal(normalizeNumberString(value, style)), true);
    }

    public static Decimal parse(String value, int style, java.util.Locale locale) {
        return new Decimal(new BigDecimal(normalizeNumberString(value, style)), true);
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
            return checked(value.divide(divisor.value));
        } catch (ArithmeticException ex) {
            return checked(value.divide(divisor.value, MAX_SCALE, RoundingMode.HALF_UP));
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
        return value.toPlainString();
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

    private static BigDecimal canonicalize(BigDecimal value) {
        BigDecimal normalized = value;
        if (normalized.scale() > MAX_SCALE) {
            normalized = normalized.setScale(MAX_SCALE, RoundingMode.HALF_EVEN);
        }

        if (normalized.scale() < 0) {
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
