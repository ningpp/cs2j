package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

/**
 * Tests for the direct integer formatting fast paths in {@link MathHelper} and
 * {@link Utf8Formatter}.
 *
 * <p>Motivation: the System.Text.Json escaping loop calls {@code TryFormat} once per escaped
 * character, so a single writeNumbers case performs 1.35M of these calls. The previous
 * implementation of every call went through {@code String.format} (or
 * {@code toHexString} + StringBuilder + {@code getBytes(US_ASCII)}), allocating several
 * objects per escaped character.
 *
 * <p>The tests pin the *existing* output, including its quirks, so the optimization cannot
 * silently change formatting.
 */
class NumericTryFormatFastPathTest {

    private static final int[] INT_VALUES = {
            0, 1, -1, 9, 10, -10, 15, 16, 62, 255, 256, 4096, -4096, 12345, -12345,
            0xABCD, -0x1234, 100000, -100000, Integer.MAX_VALUE, Integer.MIN_VALUE
    };

    // ---- MathHelper.tryFormatInt(value, dest, written, String) ----

    private static String tryFormatInt(int value, String format) {
        return tryFormatInt(value, format, 64);
    }

    private static String tryFormatInt(int value, String format, int destinationLength) {
        Span<Character> destination = new Span<>(new Character[destinationLength]);
        IntHolder written = new IntHolder();
        boolean ok = MathHelper.tryFormatInt(value, destination, written, format);
        assertTrue(ok, "did not fit: value=" + value + ", format=" + format);
        return textOf(destination, written.value);
    }

    private static String tryFormatIntSpan(int value, String format) {
        Span<Character> destination = new Span<>(new Character[64]);
        IntHolder written = new IntHolder();
        ReadOnlySpan<Character> formatSpan = MemoryExtensions.asSpan(format);
        boolean ok = MathHelper.tryFormatInt(value, destination, written, formatSpan);
        assertTrue(ok, "did not fit: value=" + value + ", format=" + format);
        return textOf(destination, written.value);
    }

    private static String textOf(Span<Character> span, int length) {
        StringBuilder builder = new StringBuilder(length);
        for (int i = 0; i < length; i++) {
            builder.append((char) span.get(i));
        }
        return builder.toString();
    }

    @Test
    void hexFormatsMatchTheReferenceImplementation() {
        String[] formats = { "X", "x", "X2", "X4", "X8", "X12", "x4" };
        for (String format : formats) {
            for (int value : INT_VALUES) {
                String expected = MathHelper.formatNumeric(format, value);
                assertEquals(expected, tryFormatInt(value, format),
                        "format=" + format + ", value=" + value);
                assertEquals(expected, tryFormatIntSpan(value, format),
                        "span format=" + format + ", value=" + value);
            }
        }
    }

    @Test
    void decimalFormatsMatchTheReferenceImplementation() {
        String[] formats = { null, "", "D", "d", "D2", "D4", "D8", "D12", "X4" };
        for (String format : formats) {
            for (int value : INT_VALUES) {
                String expected = MathHelper.formatNumeric(format, value);
                assertEquals(expected, tryFormatInt(value, format),
                        "format=" + format + ", value=" + value);
                assertEquals(expected, tryFormatIntSpan(value, format),
                        "span format=" + format + ", value=" + value);
            }
        }
    }

    @Test
    void unsupportedFormatsStillWorkThroughTheFallback() {
        // Formats the fast path deliberately rejects; each must still produce exactly what the
        // reference implementation (formatValue) produces. Float-only specifiers such as "F2"
        // are excluded because the reference implementation throws for Integer arguments (a
        // pre-existing behaviour that is not changed here).
        String[] formats = { "R", "#.##", "0.00", "X-1", "D-1" };
        for (String format : formats) {
            for (int value : new int[] { 0, 7, -7, 123456, Integer.MIN_VALUE }) {
                assertEquals(MathHelper.formatNumeric(format, value), tryFormatInt(value, format),
                        "format=" + format + ", value=" + value);
                assertEquals(MathHelper.formatNumeric(format, value), tryFormatIntSpan(value, format),
                        "span format=" + format + ", value=" + value);
            }
        }
    }

    /** The JSON writer's escape path passes the format as a span ("X4") built from a constant. */
    @Test
    void spanFormattedHexUsedByTheEscapingLoopIsCorrect() {
        assertEquals("003E", tryFormatIntSpan('>', "X4"));
        assertEquals("003C", tryFormatIntSpan('<', "X4"));
        assertEquals("0026", tryFormatIntSpan('&', "X4"));
        assertEquals("0009", tryFormatIntSpan('\t', "X4"));
        assertEquals("000A", tryFormatIntSpan('\n', "X4"));
    }

    @Test
    void destinationTooSmallIsReportedWithoutWriting() {
        Span<Character> destination = new Span<>(new Character[2]);
        IntHolder written = new IntHolder();
        written.value = -1;

        // Neither the fast path nor the fallback writes when the value does not fit.
        assertFalse(MathHelper.tryFormatInt(12345, destination, written, "D"));
        assertEquals(-1, written.value);

        assertTrue(MathHelper.tryFormatInt(12, destination, written, "D"));
        assertEquals(2, written.value);
    }

    // ---- Utf8Formatter.TryFormat(int, dest, written[, StandardFormat]) ----

    private static String tryFormatUtf8Int(int value, StandardFormat format) {
        Span<Integer> destination = new Span<>(new Integer[32]);
        IntHolder written = new IntHolder();
        boolean ok = format == null
                ? Utf8Formatter.tryFormat(value, destination, written)
                : Utf8Formatter.tryFormat(value, destination, written, format);
        assertTrue(ok, "did not fit: value=" + value);
        StringBuilder builder = new StringBuilder(written.value);
        for (int i = 0; i < written.value; i++) {
            builder.append((char) (int) destination.get(i));
        }
        return builder.toString();
    }

    @Test
    void utf8FormatterKeepsItsExistingIntegerBehaviour() {
        assertEquals("0", tryFormatUtf8Int(0, null));
        assertEquals("-1", tryFormatUtf8Int(-1, null));
        assertEquals("-2147483648", tryFormatUtf8Int(Integer.MIN_VALUE, null));
        assertEquals("2147483647", tryFormatUtf8Int(Integer.MAX_VALUE, null));
        assertEquals("12345", tryFormatUtf8Int(12345, null));

        // hex: Integer.toHexString semantics, two's complement for negatives
        assertEquals("3E", tryFormatUtf8Int(62, new StandardFormat('X')));
        assertEquals("3e", tryFormatUtf8Int(62, new StandardFormat('x')));
        assertEquals("003E", tryFormatUtf8Int(62, new StandardFormat('X', 4)));
        assertEquals("003e", tryFormatUtf8Int(62, new StandardFormat('x', 4)));
        assertEquals("FFFFFFFF", tryFormatUtf8Int(-1, new StandardFormat('X')));
        assertEquals("ffffffff", tryFormatUtf8Int(-1, new StandardFormat('x', 4)));
        assertEquals("0000ABCD", tryFormatUtf8Int(0xABCD, new StandardFormat('X', 8)));
        // Integer.toHexString never truncates, and a width smaller than the digits adds nothing
        assertEquals("ABCD", tryFormatUtf8Int(0xABCD, new StandardFormat('X', 4)));

        // decimal padding stays to the LEFT of the sign (pre-existing quirk, kept on purpose)
        assertEquals("0012", tryFormatUtf8Int(12, new StandardFormat('D', 4)));
        assertEquals("00-1", tryFormatUtf8Int(-1, new StandardFormat('D', 4)));
        assertEquals("-1", tryFormatUtf8Int(-1, new StandardFormat('D')));
        assertEquals("12345", tryFormatUtf8Int(12345, new StandardFormat('D', 4)));
        // unknown symbols fall back to plain decimal
        assertEquals("12345", tryFormatUtf8Int(12345, new StandardFormat('G')));
    }

    @Test
    void parseBuiltFormatMatchesDocumentedBehaviour() {
        assertEquals("003E", tryFormatUtf8Int(62, StandardFormat.parse("X4")));
        assertEquals("0012", tryFormatUtf8Int(12, StandardFormat.parse("D4")));
    }

    @Test
    void utf8FormatterReportsTooSmallDestinationWithoutPartialWrites() {
        Span<Integer> destination = new Span<>(new Integer[2]);
        IntHolder written = new IntHolder();
        written.value = 7;

        assertFalse(Utf8Formatter.tryFormat(12345, destination, written, new StandardFormat('D')));
        assertEquals(0, written.value);

        assertFalse(Utf8Formatter.tryFormat(-1, destination, written, new StandardFormat('X', 8)));
        assertEquals(0, written.value);

        assertTrue(Utf8Formatter.tryFormat(7, destination, written, new StandardFormat('D')));
        assertEquals(1, written.value);
    }
}
