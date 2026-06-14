package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

// Cross-validated against C# output from CSharpValidation tool
class CSharpDateTimeOffsetTest {

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
}
