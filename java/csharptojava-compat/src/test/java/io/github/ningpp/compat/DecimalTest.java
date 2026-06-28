package io.github.ningpp.compat;

import java.math.BigDecimal;
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
}
