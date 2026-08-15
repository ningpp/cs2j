package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class Utf8FormatterDateTimeTest {

    @Test
    void tryFormat_DateTime_RoundTripFormat_HasSevenFractionDigits() {
        CSharpDateTime value = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 123, DateTimeKind.Utc);
        Span<Integer> destination = new Span<>(new byte[64]);
        IntHolder written = new IntHolder();
        boolean ok = Utf8Formatter.tryFormat(value, destination, written, new StandardFormat('O'));
        assertTrue(ok);
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < written.value; i++) {
            sb.append((char) (destination.get(i) & 0xFF));
        }
        String text = sb.toString();
        assertEquals(28, text.length());
        assertTrue(text.startsWith("2024-06-15T10:30:45.123"), text);
        assertTrue(text.endsWith("Z"), text);
    }

    @Test
    void tryFormat_DateTimeOffset_RoundTripFormat_HasOffset() {
        CSharpDateTimeOffset value = CSharpDateTimeOffset.parse("2024-06-15T10:30:45.1234567+08:00");
        Span<Integer> destination = new Span<>(new byte[64]);
        IntHolder written = new IntHolder();
        boolean ok = Utf8Formatter.tryFormat(value, destination, written, new StandardFormat('O'));
        assertTrue(ok);
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < written.value; i++) {
            sb.append((char) (destination.get(i) & 0xFF));
        }
        String text = sb.toString();
        assertTrue(text.startsWith("2024-06-15T10:30:45.1234567"), text);
        assertTrue(text.endsWith("+08:00"), text);
    }

    @Test
    void toString_CustomFormatWithUnquotedT_QuotesLiteralT() {
        CSharpDateTime value = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 0, DateTimeKind.Utc);
        String text = value.toString("yyyy-MM-ddTHH:mm:ss");
        assertEquals("2024-06-15T10:30:45", text);
    }
}
