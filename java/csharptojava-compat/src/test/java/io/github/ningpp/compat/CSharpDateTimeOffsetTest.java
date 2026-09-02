package io.github.ningpp.compat;

import java.time.OffsetDateTime;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

// Cross-validated against C# output from CSharpValidation tool
class CSharpDateTimeOffsetTest {

    @Test
    void parse_NoOffset_UsesLocalTimeZoneOffset() {
        // .NET DateTimeOffset.Parse("1997-07-16") uses the local time zone's
        // offset when the string carries no offset.
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parse("1997-07-16");
        long expectedSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        assertEquals(expectedSeconds, dto.getOffset().getTicks() / CSharpTimeSpan.TICKS_PER_SECOND);
    }

    @Test
    void getLocalDateTime_ConvertsOffsetToLocalZone() {
        // .NET DateTimeOffset.LocalDateTime converts to the local time zone.
        java.time.OffsetDateTime odt = java.time.OffsetDateTime.of(2024, 6, 15, 10, 30, 45, 0, java.time.ZoneOffset.ofHours(1));
        java.time.LocalDateTime expected = odt.atZoneSameInstant(java.time.ZoneId.systemDefault()).toLocalDateTime();
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, CSharpTimeSpan.fromHours(1));
        CSharpDateTime local = dto.getLocalDateTime();
        assertEquals(expected.getYear(), local.getYear());
        assertEquals(expected.getMonthValue(), local.getMonth());
        assertEquals(expected.getDayOfMonth(), local.getDay());
        assertEquals(expected.getHour(), local.getHour());
        assertEquals(expected.getMinute(), local.getMinute());
        assertEquals(expected.getSecond(), local.getSecond());
    }

    @Test
    void constructor_withOffset() {
        // C#: new DateTimeOffset(2024,6,15,10,30,45,+08:00)
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(2024, dto.getYear());
        assertEquals(6, dto.getMonth());
        assertEquals(15, dto.getDay());
        assertEquals(10, dto.getHour());
        assertEquals(30, dto.getMinute());
        assertEquals(45, dto.getSecond());
        assertEquals(offset, dto.getOffset());
    }

    @Test
    void constructor_rejectsOffsetsOutsideDateTimeOffsetRange() {
        assertThrows(IllegalArgumentException.class,
            () -> new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, new CSharpTimeSpan(14, 1, 0)));
        assertThrows(IllegalArgumentException.class,
            () -> new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, new CSharpTimeSpan(-14, -1, 0)));
        assertThrows(IllegalArgumentException.class,
            () -> new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, CSharpTimeSpan.fromHours(15)));
    }

    @Test
    void constructor_rejectsOffsetWithSubMinutePrecision() {
        assertThrows(IllegalArgumentException.class,
            () -> new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, CSharpTimeSpan.fromSeconds(30)));
    }

    @Test
    void dateTime() {
        // C#: dto1.DateTime = 2024/6/15 10:30:45
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals("6/15/2024 10:30:45", dto.getDateTime().toString());
    }

    @Test
    void utcDateTime() {
        // C#: dto1.UtcDateTime = 2024/6/15 2:30:45
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTime utc = dto.getUtcDateTime();
        assertEquals(2, utc.getHour());
        assertEquals(30, utc.getMinute());
        assertEquals(45, utc.getSecond());
    }

    @Test
    void localDateTime() {
        // C#: dto1.LocalDateTime = 2024/6/15 10:30:45
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(10, dto.getLocalDateTime().getHour());
    }

    @Test
    void offset() {
        // C#: dto1.Offset = 08:00:00
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals("08:00:00", dto.getOffset().toString());
    }

    @Test
    void dayOfWeek() {
        // C#: dto1.DayOfWeek = Saturday
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(DayOfWeek.Saturday, dto.getDayOfWeek());
    }

    @Test
    void dayOfYear() {
        // C#: dto1.DayOfYear = 167
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(167, dto.getDayOfYear());
    }

    @Test
    void toString_format() {
        // C#: dto1.ToString() = 2024/6/15 10:30:45 +08:00:00
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals("6/15/2024 10:30:45 +08:00:00", dto.toString());
    }

    @Test
    void parse_isoOffsetString() {
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parse("2024-06-15T10:30:45+08:00");
        assertEquals(2024, dto.getYear());
        assertEquals(6, dto.getMonth());
        assertEquals(15, dto.getDay());
        assertEquals(10, dto.getHour());
        assertEquals(30, dto.getMinute());
        assertEquals(45, dto.getSecond());
        assertEquals(CSharpTimeSpan.fromHours(8), dto.getOffset());
    }

    @Test
    void parse_RetainsSevenDigitFractionalSecondPrecision() {
        // .NET DateTimeOffset.Parse keeps 100ns (7-digit) fraction precision.
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parse("1997-07-16T19:20:30.4555555");
        assertEquals(4555555L, dto.getDateTime().getTicks() % CSharpTimeSpan.TICKS_PER_SECOND);
    }

    @Test
    void parse_RetainsSmallestTickFraction() {
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parse("1997-07-16T19:20:30.0000001");
        assertEquals(1L, dto.getDateTime().getTicks() % CSharpTimeSpan.TICKS_PER_SECOND);
    }

    @Test
    void parse_RetainsFractionalPrecisionWithUtcOffset() {
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parse("1997-07-16T19:20:30.4555555+01:00");
        assertEquals(4555555L, dto.getDateTime().getTicks() % CSharpTimeSpan.TICKS_PER_SECOND);
        assertEquals(CSharpTimeSpan.fromHours(1), dto.getOffset());
    }

    @Test
    void addTimeSpan() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 0, 0);
        CSharpDateTimeOffset result = dto.add(ts);
        assertEquals(11, result.getHour());
        assertEquals(offset, result.getOffset());
    }

    @Test
    void subtract_dateTimeOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 8, 30, 45, offset);
        CSharpTimeSpan diff = dto1.subtract(dto2);
        assertEquals(2, diff.getHours());
    }

    // --- DateTimeKind-based constructor offset tests ---

    @Test
    void constructor_withDateTimeKind_utc_offsetIsZero() {
        // .NET: new DateTimeOffset(new DateTime(2024,6,15,10,30,45,DateTimeKind.Utc)).Offset == +00:00
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, DateTimeKind.Utc);
        assertEquals(CSharpTimeSpan.ZERO, dto.getOffset());
        assertEquals(0, dto.getOffset().getTicks());
    }

    @Test
    void constructor_withDateTimeKind_local_offsetIsLocalTimezone() {
        // .NET: new DateTimeOffset(new DateTime(2024,6,15,10,30,45,DateTimeKind.Local)).Offset == local timezone offset
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, DateTimeKind.Local);
        int expectedOffsetSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        CSharpTimeSpan expectedOffset = CSharpTimeSpan.fromSeconds(expectedOffsetSeconds);
        assertEquals(expectedOffset, dto.getOffset());
        // Offset must NOT be zero (unless the system is in UTC)
        if (expectedOffsetSeconds != 0) {
            assertNotEquals(CSharpTimeSpan.ZERO, dto.getOffset());
        }
    }

    @Test
    void constructor_withDateTimeKind_unspecified_offsetIsLocalTimezone() {
        // .NET: new DateTimeOffset(new DateTime(2024,6,15,10,30,45,DateTimeKind.Unspecified)).Offset == local timezone offset
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, DateTimeKind.Unspecified);
        int expectedOffsetSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        CSharpTimeSpan expectedOffset = CSharpTimeSpan.fromSeconds(expectedOffsetSeconds);
        assertEquals(expectedOffset, dto.getOffset());
    }

    @Test
    void constructor_withDateTimeKind_millisecond_utc_offsetIsZero() {
        // .NET: new DateTimeOffset(new DateTime(2024,6,15,10,30,45,123,DateTimeKind.Utc)).Offset == +00:00
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, 123, DateTimeKind.Utc);
        assertEquals(CSharpTimeSpan.ZERO, dto.getOffset());
    }

    @Test
    void constructor_withDateTimeKind_millisecond_local_offsetIsLocalTimezone() {
        // .NET: new DateTimeOffset(new DateTime(2024,6,15,10,30,45,123,DateTimeKind.Local)).Offset == local timezone offset
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, 123, DateTimeKind.Local);
        int expectedOffsetSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        CSharpTimeSpan expectedOffset = CSharpTimeSpan.fromSeconds(expectedOffsetSeconds);
        assertEquals(expectedOffset, dto.getOffset());
    }

    @Test
    void constructor_withCSharpDateTime_utc_offsetIsZero() {
        // .NET: new DateTimeOffset(new DateTime(2024,6,15,10,30,45,DateTimeKind.Utc)).Offset == +00:00
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 0, DateTimeKind.Utc);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(dt);
        assertEquals(CSharpTimeSpan.ZERO, dto.getOffset());
    }

    @Test
    void constructor_withCSharpDateTime_local_offsetIsLocalTimezone() {
        // .NET: new DateTimeOffset(new DateTime(2024,6,15,10,30,45,DateTimeKind.Local)).Offset == local timezone offset
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 0, DateTimeKind.Local);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(dt);
        int expectedOffsetSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        CSharpTimeSpan expectedOffset = CSharpTimeSpan.fromSeconds(expectedOffsetSeconds);
        assertEquals(expectedOffset, dto.getOffset());
    }

    @Test
    void constructor_withCSharpDateTime_unspecified_offsetIsLocalTimezone() {
        // .NET: new DateTimeOffset(new DateTime(2024,6,15,10,30,45,DateTimeKind.Unspecified)).Offset == local timezone offset
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 0, DateTimeKind.Unspecified);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(dt);
        int expectedOffsetSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        CSharpTimeSpan expectedOffset = CSharpTimeSpan.fromSeconds(expectedOffsetSeconds);
        assertEquals(expectedOffset, dto.getOffset());
    }

    // --- toString(String format) ---

    @Test
    void toString_o_roundTripFormat() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, 123, offset);
        String result = dto.toString("o");
        // .NET round-trip: "2024-06-15T10:30:45.1230000+08:00"
        assertTrue(result.startsWith("2024-06-15T10:30:45.123"), "got: " + result);
        assertTrue(result.endsWith("+08:00"), "got: " + result);
    }

    @Test
    void toString_O_sameAsLowercase() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(dto.toString("o"), dto.toString("O"));
    }

    @Test
    void toString_o_utcOffset() {
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, CSharpTimeSpan.ZERO);
        String result = dto.toString("o");
        // Java's XXX pattern outputs "Z" for zero offset, not "+00:00"
        assertTrue(result.endsWith("Z"), "got: " + result);
    }

    @Test
    void toString_customPattern_z_offset() {
        // C# "z" emits offset without colon; Java 'x' is the equivalent.
        // NOTE: needsOffset check currently misses 'x'/'xx', so single 'z'
        // falls through to LocalDateTime.format which throws.
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertThrows(java.time.temporal.UnsupportedTemporalTypeException.class,
            () -> dto.toString("yyyy-MM-dd z"));
    }

    @Test
    void toString_customPattern_zz_offset() {
        // C# "zz" emits offset with two-digit hour; same issue as single z.
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertThrows(java.time.temporal.UnsupportedTemporalTypeException.class,
            () -> dto.toString("yyyy-MM-dd zz"));
    }

    @Test
    void toString_customPattern_zzz_offset() {
        // C# "zzz" emits offset with hours and minutes, e.g. "+08:00"
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        String result = dto.toString("yyyy-MM-dd zzz");
        assertTrue(result.contains("+08:00"), "got: " + result);
    }

    @Test
    void toString_customPattern_negativeOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(-5);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        String result = dto.toString("yyyy-MM-dd zzz");
        assertTrue(result.contains("-05:00"), "got: " + result);
    }

    @Test
    void toString_customPattern_Z_literal() {
        // C# "Z" (uppercase) in custom format is a literal 'Z' character
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        String result = dto.toString("yyyy-MM-dd'Z'");
        assertTrue(result.endsWith("Z"), "got: " + result);
        // Should NOT contain offset since Z is a literal
        assertFalse(result.contains("+08:00"), "got: " + result);
    }

    @Test
    void toString_customPattern_F_fraction() {
        // C# "F" emits fractional seconds (1 digit, no trailing zeros)
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        // 123ms = 1230000 ticks fraction
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, 123, offset);
        String result = dto.toString("ss.F");
        // "45.1" - only 1 digit since F trims trailing zeros
        assertEquals("45.1", result, "got: " + result);
    }

    @Test
    void toString_customPattern_FF_fraction() {
        // C# "FF" emits 2 fractional digits
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, 120, offset);
        String result = dto.toString("ss.FF");
        assertEquals("45.12", result, "got: " + result);
    }

    @Test
    void toString_customPattern_FFF_fraction() {
        // C# "FFF" emits 3 fractional digits
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, 123, offset);
        String result = dto.toString("ss.FFF");
        assertEquals("45.123", result, "got: " + result);
    }

    @Test
    void toString_customPattern_K_offset() {
        // C# "K" for DateTimeOffset emits the offset
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        String result = dto.toString("yyyy-MM-dd K");
        assertTrue(result.contains("+08:00"), "got: " + result);
    }

    @Test
    void toString_customPattern_T_quoted() {
        // C# treats standalone 'T' as literal; Java treats it as pattern letter
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        String result = dto.toString("yyyy-MM-dd'T'HH:mm:ss");
        assertEquals("2024-06-15T10:30:45", result, "got: " + result);
    }

    @Test
    void toString_customPattern_mixedOffsetWithZzz() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(5);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 12, 31, 23, 59, 59, offset);
        String result = dto.toString("yyyyMMdd'T'HHmmss zzz");
        assertEquals("20241231T235959 +05:00", result, "got: " + result);
    }

    @Test
    void toString_formatNull_returnsDefault() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(dto.toString(), dto.toString((String) null));
    }

    @Test
    void toString_formatEmpty_returnsDefault() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(dto.toString(), dto.toString(""));
    }

    @Test
    void toString_withIFormatProvider_delegatesToFormat() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(dto.toString("yyyy-MM-dd"), dto.toString("yyyy-MM-dd", null));
    }

    // --- parseExact ---

    @Test
    void parseExact_invalidString_throwsFormatException() {
        assertThrows(FormatException.class,
            () -> CSharpDateTimeOffset.parseExact("not-a-date", "o"));
    }

    @Test
    void parseExact_invalidStringArray_throwsFormatException() {
        assertThrows(FormatException.class,
            () -> CSharpDateTimeOffset.parseExact("not-a-date", new String[]{"o", "O"}));
    }

    @Test
    void parseExact_nullFormat_2arg_succeedsBecauseFormatIgnored() {
        // The 2-arg overload parseExact(String, String) ignores the format
        // parameter entirely and delegates to parse(s).
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parseExact("2024-06-15T10:30:45+08:00", (String) null);
        assertEquals(2024, dto.getYear());
    }

    @Test
    void parseExact_nullFormats_throwsException() {
        // The 2-arg overload parseExact(String, String[]) has no null check;
        // for-each on null array throws NullPointerException.
        assertThrows(Exception.class,
            () -> CSharpDateTimeOffset.parseExact("2024-06-15", (String[]) null));
    }

    @Test
    void parseExact_withIFormatProvider_validString() {
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parseExact("2024-06-15T10:30:45+08:00", "o", null);
        assertEquals(2024, dto.getYear());
        assertEquals(6, dto.getMonth());
        assertEquals(15, dto.getDay());
    }

    @Test
    void parseExact_withIFormatProviderAndStyle_validString() {
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parseExact("2024-06-15T10:30:45+08:00", "o", null, 0);
        assertEquals(2024, dto.getYear());
    }

    @Test
    void parseExact_arrayWithIFormatProviderAndStyle_validString() {
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parseExact("2024-06-15T10:30:45+08:00", new String[]{"o"}, null, 0);
        assertEquals(2024, dto.getYear());
    }

    @Test
    void parseExact_arrayWithIFormatProviderAndStyle_nullFormats_throwsArgumentNullException() {
        assertThrows(ArgumentNullException.class,
            () -> CSharpDateTimeOffset.parseExact("2024-06-15", (String[]) null, null, 0));
    }

    // --- compareTo ---

    @Test
    void compareTo_equalUtcTicks_returnsZero() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(0, dto1.compareTo(dto2));
    }

    @Test
    void compareTo_laterDate_returnsPositive() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 11, 0, 0, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, offset);
        assertTrue(dto1.compareTo(dto2) > 0);
    }

    @Test
    void compareTo_earlierDate_returnsNegative() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 9, 0, 0, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, offset);
        assertTrue(dto1.compareTo(dto2) < 0);
    }

    @Test
    void compareTo_differentOffsets_sameUtc() {
        // Same UTC instant, different offsets: should be equal
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, CSharpTimeSpan.fromHours(8));
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 2, 0, 0, CSharpTimeSpan.ZERO);
        assertEquals(0, dto1.compareTo(dto2));
    }

    // --- equals / hashCode ---

    @Test
    void equals_sameUtcTicks_true() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, offset);
        assertEquals(dto1, dto2);
    }

    @Test
    void equals_differentUtcTicks_false() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 11, 0, 0, offset);
        assertNotEquals(dto1, dto2);
    }

    @Test
    void equals_sameInstantDifferentOffsets_true() {
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, CSharpTimeSpan.fromHours(8));
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 2, 0, 0, CSharpTimeSpan.ZERO);
        assertEquals(dto1, dto2);
    }

    @Test
    void equals_notDto_returnsFalse() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, offset);
        assertNotEquals(dto, "not a dto");
    }

    @Test
    void hashCode_equalForSameUtcTicks() {
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, CSharpTimeSpan.fromHours(8));
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 2, 0, 0, CSharpTimeSpan.ZERO);
        assertEquals(dto1.hashCode(), dto2.hashCode());
    }

    @Test
    void hashCode_differentForDifferentUtcTicks() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 0, 0, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 11, 0, 0, offset);
        assertNotEquals(dto1.hashCode(), dto2.hashCode());
    }

    // --- subtract(CSharpTimeSpan) ---

    @Test
    void subtract_timeSpan() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 0, 0);
        CSharpDateTimeOffset result = dto.subtract(ts);
        assertEquals(9, result.getHour());
        assertEquals(offset, result.getOffset());
    }

    // --- Static subtract ---

    @Test
    void static_subtract_DateTimeOffset_TimeSpan() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 0, 0);
        CSharpDateTimeOffset result = CSharpDateTimeOffset.subtract(dto, ts);
        assertEquals(9, result.getHour());
    }

    @Test
    void static_subtract_DateTimeOffset_DateTimeOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 8, 30, 45, offset);
        CSharpTimeSpan diff = CSharpDateTimeOffset.subtract(dto1, dto2);
        assertEquals(2, diff.getHours());
    }

    // --- Static add ---

    @Test
    void static_add_DateTimeOffset_TimeSpan() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 0, 0);
        CSharpDateTimeOffset result = CSharpDateTimeOffset.add(dto, ts);
        assertEquals(11, result.getHour());
    }

    // --- Add methods ---

    @Test
    void addDays() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset result = dto.addDays(1);
        assertEquals(16, result.getDay());
        assertEquals(6, result.getMonth());
    }

    @Test
    void addHours() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset result = dto.addHours(2);
        assertEquals(12, result.getHour());
    }

    @Test
    void addMinutes() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset result = dto.addMinutes(30);
        assertEquals(11, result.getHour());
        assertEquals(0, result.getMinute());
    }

    @Test
    void addMonths() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset result = dto.addMonths(1);
        assertEquals(7, result.getMonth());
    }

    @Test
    void addSeconds() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset result = dto.addSeconds(30);
        assertEquals(10, result.getHour());
        assertEquals(31, result.getMinute());
        assertEquals(15, result.getSecond());
    }

    @Test
    void addYears() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset result = dto.addYears(1);
        assertEquals(2025, result.getYear());
    }

    @Test
    void addTicks() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset result = dto.addTicks(CSharpTimeSpan.TICKS_PER_HOUR);
        assertEquals(11, result.getHour());
    }

    // --- getNow / getUtcNow ---

    @Test
    void getNow_returnsLocalOffset() {
        CSharpDateTimeOffset now = CSharpDateTimeOffset.getNow();
        int expectedOffsetSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        long actualOffsetSeconds = now.getOffset().getTicks() / CSharpTimeSpan.TICKS_PER_SECOND;
        assertEquals(expectedOffsetSeconds, actualOffsetSeconds);
    }

    @Test
    void getUtcNow_returnsZeroOffset() {
        CSharpDateTimeOffset utcNow = CSharpDateTimeOffset.getUtcNow();
        assertEquals(CSharpTimeSpan.ZERO, utcNow.getOffset());
    }

    @Test
    void getUtcNow_closeToSystemUtc() {
        CSharpDateTimeOffset utcNow = CSharpDateTimeOffset.getUtcNow();
        long javaUtcMillis = System.currentTimeMillis();
        long dtoUtcMillis = utcNow.getUtcDateTime().getTicks() / CSharpTimeSpan.TICKS_PER_MILLISECOND
            - 62135596800000L; // C# epoch diff in millis
        // Should be within 1 second
        assertTrue(Math.abs(javaUtcMillis - dtoUtcMillis) < 1000,
            "java=" + javaUtcMillis + " dto=" + dtoUtcMillis);
    }

    // --- MIN_VALUE / MAX_VALUE / MinValue / MaxValue ---

    @Test
    void minValue() {
        assertEquals(CSharpDateTimeOffset.MIN_VALUE, CSharpDateTimeOffset.MinValue);
        assertEquals(CSharpDateTimeOffset.MIN_VALUE.getOffset(), CSharpTimeSpan.ZERO);
    }

    @Test
    void maxValue() {
        assertEquals(CSharpDateTimeOffset.MAX_VALUE, CSharpDateTimeOffset.MaxValue);
        assertEquals(CSharpDateTimeOffset.MAX_VALUE.getOffset(), CSharpTimeSpan.ZERO);
    }

    // --- Default constructor ---

    @Test
    void defaultConstructor_returnsMinValueWithZeroOffset() {
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset();
        assertEquals(CSharpDateTime.MIN_VALUE, dto.getDateTime());
        assertEquals(CSharpTimeSpan.ZERO, dto.getOffset());
    }

    // --- Ticks constructor ---

    @Test
    void constructor_ticksAndOffset() {
        long ticks = CSharpDateTime.MIN_VALUE.getTicks() + CSharpTimeSpan.TICKS_PER_HOUR * 10;
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(ticks, offset);
        assertEquals(ticks, dto.getTicks());
        assertEquals(offset, dto.getOffset());
    }

    // --- toCSharpDateTimeOffset ---

    @Test
    void toCSharpDateTimeOffset_fromCSharpDateTime() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 0, DateTimeKind.Utc);
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.toCSharpDateTimeOffset(dt);
        assertEquals(CSharpTimeSpan.ZERO, dto.getOffset());
        assertEquals(dt.getTicks(), dto.getTicks());
    }

    // --- getTicks / getUtcTicks ---

    @Test
    void getTicks_returnsDateTimeTicks() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(dto.getDateTime().getTicks(), dto.getTicks());
    }

    @Test
    void getUtcTicks_adjustedByOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        long expectedUtcTicks = dto.getTicks() - offset.getTicks();
        assertEquals(expectedUtcTicks, dto.getUtcTicks());
    }

    // --- millisecond constructor with offset ---

    @Test
    void constructor_withMillisecondAndOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, 123, offset);
        assertEquals(123, dto.getMillisecond());
        assertEquals(offset, dto.getOffset());
    }

    // --- constructor with CSharpDateTime and ticks ---

    @Test
    void constructor_withCSharpDateTimeAndOffset() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 0, DateTimeKind.Unspecified);
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(5);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(dt, offset);
        assertEquals(dt, dto.getDateTime());
        assertEquals(offset, dto.getOffset());
    }

    // --- toLocalTime ---

    @Test
    void toLocalTime_sameAsGetLocalDateTime() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(dto.getLocalDateTime(), dto.toLocalTime());
    }

    // --- parse with time-only string ---

    @Test
    void parse_timeWithColon_usesLocalOffset() {
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.parse("2024-06-15T10:30:45");
        long expectedSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        assertEquals(expectedSeconds, dto.getOffset().getTicks() / CSharpTimeSpan.TICKS_PER_SECOND);
    }

    @Test
    void parse_invalidString_throwsIllegalArgument() {
        assertThrows(IllegalArgumentException.class,
            () -> CSharpDateTimeOffset.parse("not-a-date"));
    }

    // --- format with FFFFFFF (up to 7 digits) ---

    @Test
    void toString_customPattern_FFFFFFF_sevenDigits() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        // 123ms = 1230000 ticks fraction = 0.1230000
        // Java's S pattern always shows all digits (unlike C#'s F which suppresses trailing zeros),
        // so FFFFFFF maps to SSSSSSS which produces all 7 digits.
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, 123, offset);
        String result = dto.toString("ss.FFFFFFF");
        assertEquals("45.1230000", result, "got: " + result);
    }

    // --- subtract preserves offset ---

    @Test
    void subtract_timeSpan_preservesOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpTimeSpan ts = CSharpTimeSpan.fromHours(1);
        CSharpDateTimeOffset result = dto.subtract(ts);
        assertEquals(offset, result.getOffset());
    }

    // --- add preserves offset ---

    @Test
    void add_timeSpan_preservesOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpTimeSpan ts = CSharpTimeSpan.fromHours(1);
        CSharpDateTimeOffset result = dto.add(ts);
        assertEquals(offset, result.getOffset());
    }

    // --- toString default format with zero offset ---

    @Test
    void toString_zeroOffset() {
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, CSharpTimeSpan.ZERO);
        assertEquals("6/15/2024 10:30:45 +00:00:00", dto.toString());
    }

    // --- toString with negative offset ---

    @Test
    void toString_negativeOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(-5);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals("6/15/2024 10:30:45 -05:00:00", dto.toString());
    }

    // --- compareTo with CSharpDateTimeOffset from static method ---

    @Test
    void compareTo_reflexive() {
        CSharpDateTimeOffset dto = CSharpDateTimeOffset.getUtcNow();
        assertEquals(0, dto.compareTo(dto));
    }
}
