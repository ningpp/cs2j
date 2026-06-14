package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

// Cross-validated against C# output from CSharpValidation tool
class CSharpTimeSpanTest {

    @Test
    void constructor_hms_toString() {
        // C#: new TimeSpan(1,2,3).ToString() = 01:02:03
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals("01:02:03", ts.toString());
    }

    @Test
    void properties_hms() {
        // C#: ts1.Days=0, Hours=1, Minutes=2, Seconds=3, Milliseconds=0
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals(0, ts.getDays());
        assertEquals(1, ts.getHours());
        assertEquals(2, ts.getMinutes());
        assertEquals(3, ts.getSeconds());
        assertEquals(0, ts.getMilliseconds());
    }

    @Test
    void totalValues() {
        // C#: TotalDays=0.043090277777777776, TotalHours=1.0341666666666667,
        //      TotalMinutes=62.05, TotalSeconds=3723, TotalMilliseconds=3723000
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals(0.043090277777777776, ts.getTotalDays(), 1e-12);
        assertEquals(1.0341666666666667, ts.getTotalHours(), 1e-12);
        assertEquals(62.05, ts.getTotalMinutes(), 1e-12);
        assertEquals(3723.0, ts.getTotalSeconds(), 1e-9);
        assertEquals(3723000.0, ts.getTotalMilliseconds(), 1e-6);
    }

    @Test
    void ticks() {
        // C#: ts1.Ticks = 37230000000
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals(37230000000L, ts.getTicks());
    }

    @Test
    void fromHours() {
        // C#: TimeSpan.FromHours(1.5) = 01:30:00
        assertEquals("01:30:00", CSharpTimeSpan.fromHours(1.5).toString());
    }

    @Test
    void fromMinutes() {
        // C#: TimeSpan.FromMinutes(90) = 01:30:00
        assertEquals("01:30:00", CSharpTimeSpan.fromMinutes(90).toString());
    }

    @Test
    void fromSeconds() {
        // C#: TimeSpan.FromSeconds(3661) = 01:01:01
        assertEquals("01:01:01", CSharpTimeSpan.fromSeconds(3661).toString());
    }

    @Test
    void fromMilliseconds() {
        // C#: TimeSpan.FromMilliseconds(1500) = 00:00:01.5000000
        assertEquals("00:00:01.5000000", CSharpTimeSpan.fromMilliseconds(1500).toString());
    }

    @Test
    void fromTicks() {
        // C#: TimeSpan.FromTicks(10000000) = 00:00:01
        assertEquals("00:00:01", CSharpTimeSpan.fromTicks(10000000).toString());
    }

    @Test
    void zero() {
        // C#: TimeSpan.Zero = 00:00:00
        assertEquals("00:00:00", CSharpTimeSpan.ZERO.toString());
    }

    @Test
    void add() {
        // C#: ts1.Add(ts2) = 02:32:03  (ts1=1:02:03, ts2=1:30:00)
        CSharpTimeSpan ts1 = new CSharpTimeSpan(1, 2, 3);
        CSharpTimeSpan ts2 = CSharpTimeSpan.fromHours(1.5);
        assertEquals("02:32:03", ts1.add(ts2).toString());
    }

    @Test
    void subtract() {
        // C#: ts1.Subtract(ts2) = -00:27:57
        CSharpTimeSpan ts1 = new CSharpTimeSpan(1, 2, 3);
        CSharpTimeSpan ts2 = CSharpTimeSpan.fromHours(1.5);
        assertEquals("-00:27:57", ts1.subtract(ts2).toString());
    }

    @Test
    void negate() {
        // C#: ts1.Negate() = -01:02:03
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals("-01:02:03", ts.negate().toString());
    }

    @Test
    void duration() {
        // C#: ts1.Duration() = 01:02:03
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals("01:02:03", ts.duration().toString());
    }

    @Test
    void unaryNegation() {
        // C#: (-ts1) = -01:02:03
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals("-01:02:03", ts.negate().toString());
    }

    @Test
    void compareTo() {
        // C#: ts1.CompareTo(ts2) = -1
        CSharpTimeSpan ts1 = new CSharpTimeSpan(1, 2, 3);
        CSharpTimeSpan ts2 = CSharpTimeSpan.fromHours(1.5);
        assertEquals(-1, ts1.compareTo(ts2));
    }

    @Test
    void parse_simple() {
        // C#: TimeSpan.Parse("1:02:03") = 01:02:03
        CSharpTimeSpan ts = CSharpTimeSpan.parse("1:02:03");
        assertEquals(new CSharpTimeSpan(1, 2, 3), ts);
    }

    @Test
    void parse_negative() {
        // C#: TimeSpan.Parse("-1:02:03") = -01:02:03
        CSharpTimeSpan ts = CSharpTimeSpan.parse("-1:02:03");
        assertEquals(new CSharpTimeSpan(1, 2, 3).negate(), ts);
    }

    @Test
    void parse_withDays() {
        // C#: TimeSpan.Parse("1.02:03:04.0050000") = 1.02:03:04.0050000
        CSharpTimeSpan ts = CSharpTimeSpan.parse("1.02:03:04.0050000");
        assertEquals(1, ts.getDays());
        assertEquals(2, ts.getHours());
        assertEquals(3, ts.getMinutes());
        assertEquals(4, ts.getSeconds());
        assertEquals(5, ts.getMilliseconds());
        assertEquals("1.02:03:04.0050000", ts.toString());
    }

    @Test
    void tryParse_valid() {
        ObjectHolder<CSharpTimeSpan> holder = new ObjectHolder<>();
        assertTrue(CSharpTimeSpan.tryParse("1:02:03", holder));
        assertEquals(new CSharpTimeSpan(1, 2, 3), holder.value);
    }

    @Test
    void tryParse_invalid() {
        ObjectHolder<CSharpTimeSpan> holder = new ObjectHolder<>();
        assertFalse(CSharpTimeSpan.tryParse("abc", holder));
        assertEquals(CSharpTimeSpan.ZERO, holder.value);
    }
}
