package io.github.ningpp.compat;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.assertTimeout;

/**
 * Utf8Parser.tryParseDecimal mirrors C#
 * Utf8Parser.TryParse(ReadOnlySpan&lt;byte&gt;, out decimal, out int, char), which is
 * what System.Text.Json's JsonDocument.TryGetValue(index, out decimal) runs
 * (JsonDocument.cs, "Utf8Parser.TryParse(segment, out decimal tmp, out int
 * bytesConsumed, standardFormat)" with the 'e' scientific-notation format for
 * compact numbers). The expectations below are the ones .NET 10 returns for the
 * same inputs, so a regression here means the transcode diverges from C#.
 *
 * <p>readTooPreciseDouble ("1e+100000002") covers both the reject and the
 * underflow case and used to take ~56s in the Java suite because
 * Decimal.canonicalize() built 10^100000002 as a BigInteger; these tests pin the
 * behaviour and the cost of that path.
 */
public class Utf8ParserDecimalTest {

    /** JsonDocument hands over the UTF-8 bytes of the number token. */
    private static ReadOnlySpan<Integer> utf8(String text) {
        return new ReadOnlySpan<>(text.getBytes(StandardCharsets.UTF_8));
    }

    private static boolean tryParseDecimal(String text, ObjectHolder<Decimal> value, IntHolder consumed) {
        return Utf8Parser.tryParseDecimal(utf8(text), value, consumed, 'e');
    }

    @Test
    public void tryParseDecimal_reportsBytesConsumedForRepresentableValues() {
        ObjectHolder<Decimal> value = new ObjectHolder<>();
        IntHolder consumed = new IntHolder();
        assertTrue(tryParseDecimal("1e+28", value, consumed));
        assertEquals("10000000000000000000000000000", value.value.toString());
        assertEquals(5, consumed.value);

        assertTrue(tryParseDecimal("7.9228162514264337593543950335e+28", value, consumed));
        assertEquals("79228162514264337593543950335", value.value.toString());
    }

    @Test
    public void tryParseDecimal_rejectsPositiveExponentsBeyondDecimalRange() {
        ObjectHolder<Decimal> value = new ObjectHolder<>();
        IntHolder consumed = new IntHolder();
        assertFalse(tryParseDecimal("1e+29", value, consumed));
        assertFalse(tryParseDecimal("7.9228162514264337593543950336e+28", value, consumed));
        assertFalse(tryParseDecimal("-1e+100000002", value, consumed));
    }

    @Test
    public void tryParseDecimal_underflowsHugeNegativeExponentsToZero() {
        ObjectHolder<Decimal> value = new ObjectHolder<>();
        IntHolder consumed = new IntHolder();
        assertTrue(tryParseDecimal("1e-100000002", value, consumed));
        assertEquals("0.0000000000000000000000000000", value.value.toString());
        assertEquals(12, consumed.value);

        assertTrue(tryParseDecimal("-1e-100000002", value, consumed));
        assertEquals("0.0000000000000000000000000000", value.value.toString());
    }

    /**
     * Zero is representable at any scale, so an extreme exponent must not be read as
     * an overflow: .NET reports success with 0 for "0e+100000002" and with 0E-28 for
     * "0e-100000002".
     */
    @Test
    public void tryParseDecimal_zeroSurvivesExtremeExponents() {
        ObjectHolder<Decimal> value = new ObjectHolder<>();
        IntHolder consumed = new IntHolder();
        assertTrue(tryParseDecimal("0e+100000002", value, consumed));
        assertEquals("0", value.value.toString());
        assertTrue(tryParseDecimal("0e-100000002", value, consumed));
        assertEquals("0.0000000000000000000000000000", value.value.toString());
    }

    /**
     * Regression guard: each of these calls used to spend ~40s inside
     * BigDecimal.setScale() building a power of ten with up to 100 million digits
     * (and the result was discarded right afterwards). They are O(1) now; the
     * timeout only has to be far below the old cost to catch a reintroduction.
     */
    @Test
    public void tryParseDecimal_hugeExponentsResolveInConstantTime() {
        assertTimeout(Duration.ofSeconds(5), () -> {
            ObjectHolder<Decimal> value = new ObjectHolder<>();
            IntHolder consumed = new IntHolder();
            String[] inputs = {
                "1e+100000002", "-1e+100000002", "1e-100000002", "-1e-100000002",
                "1e+2000000000", "1e-2000000000", "0e+2000000000", "0e-2000000000",
            };
            for (String input : inputs) {
                tryParseDecimal(input, value, consumed);
            }
        });
    }
}
