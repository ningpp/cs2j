package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

/**
 * .NET invariant-culture {@code double.ToString()} (shortest round-trip, "G"):
 * fixed notation when the decimal exponent is in [-4, 14], scientific notation
 * with a sign and at least two exponent digits ("E+15", "E-05") otherwise, and
 * no trailing ".0". Utf8JsonReaderTests compares parsed number values against
 * C# string literals through this helper, e.g. "1970097990".
 */
public class DoubleHelperTest {

    private static void assertDotNet(String expected, double value) {
        assertEquals(expected, DoubleHelper.toString(value),
                "DoubleHelper.toString(" + value + ")");
    }

    @Test
    public void integralValuesPrintWithoutDecimalPoint() {
        assertDotNet("1970097990", 1970097990.0);
        assertDotNet("4", 4.0);
        assertDotNet("0", 0.0);
        assertDotNet("100", 100.0);
        assertDotNet("-90210", -90210.0);
        assertDotNet("123456789012345", 123456789012345.0);
        assertDotNet("10000000", 1.0e7);
        assertDotNet("100000000000000", 1.0e14);
    }

    @Test
    public void fixedAndScientificThresholdMatchDotNet() {
        assertDotNet("0.0001", 0.0001);
        assertDotNet("1E-05", 0.00001);
        assertDotNet("999999999999999", 999999999999999.0);
        assertDotNet("1E+15", 1.0e15);
        assertDotNet("1E+16", 1.0e16);
        assertDotNet("0.001", 0.001);
        assertDotNet("1234.5", 1234.5);
    }

    @Test
    public void scientificFormHasSignAndAtLeastTwoExponentDigits() {
        assertDotNet("1.5E-07", 1.5e-7);
        assertDotNet("-1.5E-07", -1.5e-7);
        assertDotNet("1.234567890123456E+15", 1.234567890123456e15);
        assertDotNet("1E+100", 1.0e100);
        assertDotNet("5E-05", 5.0e-5);
    }

    @Test
    public void specialValuesKeepTheirNames() {
        assertDotNet("NaN", Double.NaN);
        assertDotNet("Infinity", Double.POSITIVE_INFINITY);
        assertDotNet("-Infinity", Double.NEGATIVE_INFINITY);
        assertDotNet("-1.7976931348623157E+308", -Double.MAX_VALUE);
    }
}
