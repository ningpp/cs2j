package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

// Cross-validated against C# output from CSharpValidation tool
class CSharpDateOnlyTest {

    @Test
    void constructor_ymd() {
        // C#: new DateOnly(2024,6,15)
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals(2024, d.getYear());
        assertEquals(6, d.getMonth());
        assertEquals(15, d.getDay());
    }

    @Test
    void dayOfWeek() {
        // C#: do1.DayOfWeek = Saturday
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals(DayOfWeek.Saturday, d.getDayOfWeek());
    }

    @Test
    void dayOfYear() {
        // C#: do1.DayOfYear = 167
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals(167, d.getDayOfYear());
    }

    @Test
    void dayNumber() {
        // C#: do1.DayNumber = 739051
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals(739051, d.getDayNumber());
    }

    @Test
    void addDays() {
        // C#: do1.AddDays(10) = 2024/6/25
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals("6/25/2024", d.addDays(10).toString());
    }

    @Test
    void addMonths() {
        // C#: do1.AddMonths(3) = 2024/9/15
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals("9/15/2024", d.addMonths(3).toString());
    }

    @Test
    void addYears() {
        // C#: do1.AddYears(1) = 2025/6/15
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals("6/15/2025", d.addYears(1).toString());
    }

    @Test
    void fromDateTime() {
        // C#: DateOnly.FromDateTime(dt1) = 2024/6/15
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateOnly d = CSharpDateOnly.fromDateTime(dt);
        assertEquals("6/15/2024", d.toString());
    }

    @Test
    void parse() {
        // C#: DateOnly.Parse("2024-06-15") = 2024/6/15
        CSharpDateOnly d = CSharpDateOnly.parse("2024-06-15");
        assertEquals("6/15/2024", d.toString());
    }

    @Test
    void toString_format() {
        // C#: new DateOnly(2024,6,15).ToString() = 2024/6/15
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals("6/15/2024", d.toString());
    }

    @Test
    void fromDayNumber() {
        CSharpDateOnly d = CSharpDateOnly.fromDayNumber(739051);
        assertEquals(2024, d.getYear());
        assertEquals(6, d.getMonth());
        assertEquals(15, d.getDay());
    }
}
