package io.github.ningpp.compat;

import java.math.BigDecimal;
import java.time.Duration;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class DecimalTest {

    @Test
    void constants_matchCSharpBounds() {
        assertEquals("0", Decimal.ZERO.toString());
        assertEquals("1", Decimal.ONE.toString());
        assertEquals("-1", Decimal.MINUS_ONE.toString());
        assertEquals("79228162514264337593543950335", Decimal.MAX_VALUE.toString());
        assertEquals("-79228162514264337593543950335", Decimal.MIN_VALUE.toString());
    }

    @Test
    void parse_validValues() {
        assertEquals("123.45", Decimal.parse("123.45").toString());
        assertEquals("-123.45", Decimal.parse("-123.45").toString());
        assertEquals("1.00", Decimal.parse("1.00").toString());
        assertEquals("42", Decimal.parse(" 42 ").toString());
        assertEquals("1234.5", Decimal.parse("1,234.5").toString());
        assertEquals("-123", Decimal.parse("123-").toString());
        assertEquals("123", Decimal.parse("123.").toString());
        assertEquals("0.0000000000000000000000000001",
            Decimal.parse("0.0000000000000000000000000001").toString());
    }

    @Test
    void parse_rejectsInvalidValues() {
        assertThrows(FormatException.class, () -> Decimal.parse(null));
        assertThrows(FormatException.class, () -> Decimal.parse(""));
        assertThrows(FormatException.class, () -> Decimal.parse("abc"));
        assertThrows(FormatException.class, () -> Decimal.parse("NaN"));
        assertThrows(FormatException.class, () -> Decimal.parse("Infinity"));
        assertThrows(FormatException.class, () -> Decimal.parse("1e2"));
        assertThrows(FormatException.class, () -> Decimal.parse("(123)"));
        assertEquals("0.0000000000000000000000000000",
            Decimal.parse("0.00000000000000000000000000001").toString());
        assertEquals("0.0000000000000000000000000001",
            Decimal.parse("0.00000000000000000000000000006").toString());
        assertThrows(ArithmeticException.class,
            () -> Decimal.parse("79228162514264337593543950336"));
    }

    @Test
    void tryParse_invalidSetsHolderToZero() {
        ObjectHolder<Decimal> holder = new ObjectHolder<>(Decimal.ONE);
        assertFalse(Decimal.tryParse("abc", holder));
        assertSame(Decimal.ZERO, holder.value);
    }

    @Test
    void tryParse_validSetsHolder() {
        ObjectHolder<Decimal> holder = new ObjectHolder<>();
        assertTrue(Decimal.tryParse("42.5", holder));
        assertEquals(Decimal.parse("42.5"), holder.value);
    }

    @Test
    void parseAndTryParse_acceptNumberFormatInfoProvider() {
        NumberFormatInfo provider = NumberFormatInfo.getInvariantInfo();
        assertEquals("42.5", Decimal.parse("42.5", NumberStyles.Number, provider).toString());

        ObjectHolder<Decimal> holder = new ObjectHolder<>();
        assertTrue(Decimal.tryParse("42.5", NumberStyles.Number, provider, holder));
        assertEquals(Decimal.parse("42.5"), holder.value);
    }

    @Test
    void arithmetic_basicOperations() {
        Decimal a = Decimal.parse("10.5");
        Decimal b = Decimal.parse("2");
        assertEquals("12.5", a.add(b).toString());
        assertEquals("8.5", a.subtract(b).toString());
        assertEquals("21.0", a.multiply(b).toString());
        assertEquals("5.25", a.divide(b).toString());
        assertEquals("0.5", a.remainder(b).toString());
    }

    @Test
    void arithmetic_overflowThrows() {
        assertThrows(ArithmeticException.class, () -> Decimal.MAX_VALUE.add(Decimal.ONE));
        assertThrows(ArithmeticException.class, () -> Decimal.MIN_VALUE.subtract(Decimal.ONE));
        assertThrows(ArithmeticException.class, () -> Decimal.MAX_VALUE.multiply(Decimal.parse("2")));
    }

    @Test
    void divideByZeroThrows() {
        assertThrows(ArithmeticException.class, () -> Decimal.ONE.divide(Decimal.ZERO));
    }

    @Test
    void divide_nonTerminatingRoundsToTwentyEightPlaces() {
        assertEquals("0.3333333333333333333333333333",
            Decimal.ONE.divide(Decimal.parse("3")).toString());
        assertEquals("0.6666666666666666666666666667",
            Decimal.parse("2").divide(Decimal.parse("3")).toString());
        assertEquals("0.1666666666666666666666666667",
            Decimal.ONE.divide(Decimal.parse("6")).toString());
        assertEquals("0.1428571428571428571428571429",
            Decimal.ONE.divide(Decimal.parse("7")).toString());
    }

    @Test
    void divide_tieAtScaleBoundary_roundsHalfEven() {
        // 1e-28 / 2 = 5e-29, an exact tie between 0 and 1e-28. C# decimal
        // rounds ties to even, so the result is 0 (HALF_UP would give 1e-28).
        assertEquals(0,
            Decimal.parse("0.0000000000000000000000000001")
                .divide(Decimal.parse("2")).compareTo(Decimal.ZERO));
        assertEquals(0,
            Decimal.parse("-0.0000000000000000000000000001")
                .divide(Decimal.parse("2")).compareTo(Decimal.ZERO));
        // 3e-28 / 2 = 1.5e-28: the even neighbour 2e-28 wins.
        assertEquals("0.0000000000000000000000000002",
            Decimal.parse("0.0000000000000000000000000003")
                .divide(Decimal.parse("2")).toStringPlain());
        // Sentinel from the numeric-boundary oracle: the rounded-to-zero
        // quotient reports scale 0 ("0"), not 28 trailing zeros.
        assertEquals("0", Decimal.ONE.divide(
            Decimal.parse("20000000000000000000000000000")).toString());
        // An exact zero quotient keeps the dividend's trailing-zero scale.
        assertEquals("0.0", Decimal.parse("0.0").divide(Decimal.ONE).toString());
    }

    @Test
    void staticCompare_matchesInstanceCompareTo_andIgnoresScale() {
        assertEquals(0, Decimal.compare(Decimal.parse("1.0"), Decimal.parse("1.00")));
        assertTrue(Decimal.compare(Decimal.parse("1.0"), Decimal.parse("2")) < 0);
        assertTrue(Decimal.compare(Decimal.parse("2"), Decimal.parse("1.00")) > 0);
    }

    @Test
    void roundingAndIntegralOperations_matchCSharpStyle() {
        assertEquals("2", Decimal.parse("2.5").round().toString());
        assertEquals("4", Decimal.parse("3.5").round().toString());
        assertEquals("-2", Decimal.parse("-2.5").round().toString());
        assertEquals("1.24", Decimal.parse("1.235").round(2).toString());
        assertEquals("-1", Decimal.parse("-1.9").truncate().toString());
        assertEquals("-2", Decimal.parse("-1.1").floor().toString());
        assertEquals("-1", Decimal.parse("-1.1").ceiling().toString());
    }

    @Test
    void integralConversions_validateFractionAndRange() {
        assertEquals(42, Decimal.parse("42").intValue());
        assertEquals(42, Decimal.parse("42.1").intValue());
        assertEquals(-42, Decimal.parse("-42.9").intValue());
        assertEquals(42L, Decimal.parse("42").longValue());
        assertThrows(ArithmeticException.class, () -> Decimal.parse("2147483648").intValue());
        assertThrows(ArithmeticException.class, () -> Decimal.parse("9223372036854775808").longValue());
    }

    @Test
    void equalityAndHashCode_ignoreScale() {
        Decimal a = Decimal.parse("1.0");
        Decimal b = Decimal.parse("1.00");
        assertEquals(a, b);
        assertEquals(a.hashCode(), b.hashCode());
        assertEquals(0, a.compareTo(b));
    }

    @Test
    void factories_validateInputs() {
        assertEquals("42", Decimal.valueOf(42).toString());
        assertEquals("42", Decimal.valueOf(42L).toString());
        assertEquals("42.5", Decimal.valueOf(42.5d).toString());
        assertEquals("42.500", Decimal.valueOf(new BigDecimal("42.500")).toString());
        assertThrows(NumberFormatException.class, () -> Decimal.valueOf(Double.NaN));
        assertThrows(NumberFormatException.class, () -> Decimal.valueOf(Double.POSITIVE_INFINITY));
    }

    /**
     * C# decimal is a 96-bit mantissa with a scale of at most 28, so the BCL parser
     * rejects an exponent that cannot fit by comparing integers
     * (Number.TryNumberToDecimal: "if (e &gt; DecimalPrecision) return false;", with
     * DecimalPrecision = 29) instead of scaling the value. Decimal.parseExponent is
     * the entry point Utf8Parser uses for JSON numbers, and it has to report the same
     * accept/reject decision; the expectations below are the ones .NET 10 produces for
     * the identical inputs.
     */
    @Test
    void parseExponent_boundariesMatchCSharpDecimalRange() {
        assertEquals("10000000000000000000000000000", Decimal.parseExponent("1e+28").toString());
        assertThrows(ArithmeticException.class, () -> Decimal.parseExponent("1e+29"));
        assertThrows(ArithmeticException.class, () -> Decimal.parseExponent("1e+30"));
        assertEquals("79228162514264337593543950335",
            Decimal.parseExponent("7.9228162514264337593543950335e+28").toString());
        assertThrows(ArithmeticException.class,
            () -> Decimal.parseExponent("7.9228162514264337593543950336e+28"));
        assertThrows(ArithmeticException.class, () -> Decimal.parseExponent("-1e+100000002"));
        assertThrows(ArithmeticException.class,
            () -> Decimal.valueOf(new BigDecimal("1e+100000002")));
    }

    /**
     * A value below 10^-29 is less than half of the last representable place, so
     * rounding it to 28 decimals is zero — no matter how large the exponent is.
     * 5e-29 is an exact tie and rounds half-even to zero, 6e-29 rounds up.
     */
    @Test
    void parseExponent_hugeNegativeExponentUnderflowsToZero() {
        assertEquals("0.0000000000000000000000000000", Decimal.parseExponent("1e-100000002").toString());
        assertEquals("0.0000000000000000000000000000", Decimal.parseExponent("-1e-100000002").toString());
        assertEquals("0.0000000000000000000000000000", Decimal.parseExponent("1e-29").toString());
        assertEquals("0.0000000000000000000000000000", Decimal.parseExponent("5e-29").toString());
        assertEquals("0.0000000000000000000000000001", Decimal.parseExponent("6e-29").toString());
    }

    /**
     * Zero is representable at any scale, so an extreme exponent must not be
     * reported as an overflow. .NET returns 0 for "0e+100000002" and 0E-28 for
     * "0e-100000002"; the overflow pre-check must not turn the first into a throw.
     */
    @Test
    void parseExponent_zeroWithExtremeExponentStaysZero() {
        assertEquals("0", Decimal.parseExponent("0e+100000002").toString());
        assertEquals("0", Decimal.parseExponent("0e+29").toString());
        assertEquals("0", Decimal.valueOf(new BigDecimal("0E+100000002")).toString());
        assertEquals("0.0000000000000000000000000000",
            Decimal.parseExponent("0e-100000002").toString());
    }

    /**
     * Regression guard for the O(10^|exponent|) blow-up: canonicalize() used to call
     * BigDecimal.setScale(), which materializes 10^(-scale) as a BigInteger. For
     * "1e+100000002" that is a 100_000_002 digit number (~40 MB, ~40s on a cold JVM)
     * that validateRange() then rejected, and the mirror case "1e-100000002" built
     * 10^99_999_974 only to round it to zero. Both are O(1) now, so the whole battery
     * below must finish in well under a second; the timeout is a generous guard
     * against a silent reintroduction (the old behaviour would need minutes).
     */
    @Test
    void parseExponent_hugeExponentsResolveInConstantTime() {
        assertTimeout(Duration.ofSeconds(5), () -> {
            String[] inputs = {
                "1e+100000002", "-1e+100000002", "1e-100000002", "-1e-100000002",
                "1e+2000000000", "1e-2000000000", "0e+2000000000", "0e-2000000000",
                "9".repeat(1000),
            };
            for (String input : inputs) {
                try {
                    if (input.indexOf('e') >= 0) {
                        Decimal.parseExponent(input);
                    } else {
                        Decimal.valueOf(new BigDecimal(input));
                    }
                } catch (ArithmeticException expectedOverflow) {
                    // the positive exponents above cannot be represented
                }
            }
        });
    }
}
