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
}
