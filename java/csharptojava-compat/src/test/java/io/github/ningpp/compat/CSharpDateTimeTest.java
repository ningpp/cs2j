package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

// Cross-validated against C# output from CSharpValidation tool
class CSharpDateTimeTest {

    @Test
    void constructor_ymdhms() {
        // C#: new DateTime(2024,6,15,10,30,45)
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals(2024, dt.getYear());
        assertEquals(6, dt.getMonth());
        assertEquals(15, dt.getDay());
        assertEquals(10, dt.getHour());
        assertEquals(30, dt.getMinute());
        assertEquals(45, dt.getSecond());
        assertEquals(0, dt.getMillisecond());
    }

    @Test
    void dayOfWeek() {
        // C#: dt1.DayOfWeek = Saturday
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals(DayOfWeek.Saturday, dt.getDayOfWeek());
    }

    @Test
    void dayOfYear() {
        // C#: dt1.DayOfYear = 167
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals(167, dt.getDayOfYear());
    }

    @Test
    void ticks() {
        // C#: dt1.Ticks = 638540442450000000
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals(638540442450000000L, dt.getTicks());
    }

    @Test
    void kind() {
        // C#: dt1.Kind = Unspecified
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals(DateTimeKind.Unspecified, dt.getKind());
    }

    @Test
    void date() {
        // C#: dt1.Date = 2024/6/15 0:00:00
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateTime dateOnly = dt.getDate();
        assertEquals("6/15/2024 0:00:00", dateOnly.toString());
    }

    @Test
    void timeOfDay() {
        // C#: dt1.TimeOfDay = 10:30:45
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpTimeSpan tod = dt.getTimeOfDay();
        assertEquals("10:30:45", tod.toString());
    }

    @Test
    void addDays() {
        // C#: dt1.AddDays(10) = 2024/6/25 10:30:45
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals("6/25/2024 10:30:45", dt.addDays(10).toString());
    }

    @Test
    void addHours() {
        // C#: dt1.AddHours(2) = 2024/6/15 12:30:45
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals("6/15/2024 12:30:45", dt.addHours(2).toString());
    }

    @Test
    void addMinutes() {
        // C#: dt1.AddMinutes(30) = 2024/6/15 11:00:45
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals("6/15/2024 11:00:45", dt.addMinutes(30).toString());
    }

    @Test
    void addSeconds() {
        // C#: dt1.AddSeconds(60) = 2024/6/15 10:31:45
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals("6/15/2024 10:31:45", dt.addSeconds(60).toString());
    }

    @Test
    void addMonths() {
        // C#: dt1.AddMonths(3) = 2024/9/15 10:30:45
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals("9/15/2024 10:30:45", dt.addMonths(3).toString());
    }

    @Test
    void addYears() {
        // C#: dt1.AddYears(1) = 2025/6/15 10:30:45
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals("6/15/2025 10:30:45", dt.addYears(1).toString());
    }

    @Test
    void addTimeSpan() {
        // C#: dt1.Add(ts1) = 2024/6/15 11:32:48  (ts1 = 1:02:03)
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals("6/15/2024 11:32:48", dt.add(ts).toString());
    }

    @Test
    void daysInMonth() {
        // C#: DateTime.DaysInMonth(2024, 2) = 29
        // C#: DateTime.DaysInMonth(2023, 2) = 28
        assertEquals(29, CSharpDateTime.daysInMonth(2024, 2));
        assertEquals(28, CSharpDateTime.daysInMonth(2023, 2));
    }

    @Test
    void isLeapYear() {
        // C#: DateTime.IsLeapYear(2024) = True
        // C#: DateTime.IsLeapYear(2023) = False
        assertTrue(CSharpDateTime.isLeapYear(2024));
        assertFalse(CSharpDateTime.isLeapYear(2023));
    }

    @Test
    void parse_date() {
        // C#: DateTime.Parse("2024-06-15") = 2024/6/15 0:00:00
        CSharpDateTime dt = CSharpDateTime.parse("2024-06-15");
        assertEquals("6/15/2024 0:00:00", dt.toString());
    }

    @Test
    void parse_dateTime() {
        // C#: DateTime.Parse("2024-06-15T10:30:45") = 2024/6/15 10:30:45
        CSharpDateTime dt = CSharpDateTime.parse("2024-06-15T10:30:45");
        assertEquals("6/15/2024 10:30:45", dt.toString());
    }

    @Test
    void subtract_dateTime() {
        CSharpDateTime dt1 = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateTime dt2 = new CSharpDateTime(2024, 6, 15, 8, 0, 0);
        CSharpTimeSpan diff = dt1.subtract(dt2);
        assertEquals(2, diff.getHours());
        assertEquals(30, diff.getMinutes());
        assertEquals(45, diff.getSeconds());
    }

    @Test
    void toString_default() {
        // C#: new DateTime(2024,6,15,10,30,45).ToString() = 2024/6/15 10:30:45
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals("6/15/2024 10:30:45", dt.toString());
    }

    @Test
    void toString_roundtripFormat() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 123);
        assertEquals("2024-06-15T10:30:45.1230000", dt.toString("o"));
    }
}
