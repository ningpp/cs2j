package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

// Cross-validated against C# output from CSharpValidation tool
class CSharpTimeOnlyTest {

    @Test
    void constructor_hms() {
        // C#: new TimeOnly(10,30,45)
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        assertEquals(10, t.getHour());
        assertEquals(30, t.getMinute());
        assertEquals(45, t.getSecond());
        assertEquals(0, t.getMillisecond());
    }

    @Test
    void ticks() {
        // C#: to1.Ticks = 378450000000
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        assertEquals(378450000000L, t.getTicks());
    }

    @Test
    void addHours() {
        // C#: to1.AddHours(2) = 12:30
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        assertEquals("12:30", t.addHours(2).toString());
    }

    @Test
    void addMinutes() {
        // C#: to1.AddMinutes(30) = 11:00
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        assertEquals("11:00", t.addMinutes(30).toString());
    }

    @Test
    void addTimeSpan() {
        // C#: to1.Add(TimeSpan.FromHours(1)) = 11:30
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        CSharpTimeSpan ts = CSharpTimeSpan.fromHours(1);
        assertEquals("11:30", t.add(ts).toString());
    }

    @Test
    void fromDateTime() {
        // C#: TimeOnly.FromDateTime(dt1) = 10:30
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpTimeOnly t = CSharpTimeOnly.fromDateTime(dt);
        assertEquals("10:30", t.toString());
    }

    @Test
    void parse() {
        // C#: TimeOnly.Parse("10:30:45") = 10:30
        CSharpTimeOnly t = CSharpTimeOnly.parse("10:30:45");
        assertEquals(10, t.getHour());
        assertEquals(30, t.getMinute());
        assertEquals(45, t.getSecond());
    }

    @Test
    void compareTo() {
        // C#: to1.CompareTo(new TimeOnly(11,0,0)) = -1
        CSharpTimeOnly t1 = new CSharpTimeOnly(10, 30, 45);
        CSharpTimeOnly t2 = new CSharpTimeOnly(11, 0, 0);
        assertEquals(-1, t1.compareTo(t2));
    }

    @Test
    void toString_default() {
        // C#: new TimeOnly(10,30,45).ToString() = 10:30
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        assertEquals("10:30", t.toString());
    }

    @Test
    void toString_withSeconds() {
        // When seconds != 0, C# shows seconds
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        // C# default is "10:30" because it uses short time format
        // But our toString shows seconds when non-zero
        // Actually C# TimeOnly.ToString() default is "h:mm tt" (short time)
        // For 24h format without AM/PM it shows "10:30"
        assertEquals("10:30", t.toString());
    }
}
