package dotnet.wpf.swing;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class SizeTest {

    @Test
    void defaultConstructor_isZero() {
        Size s = new Size();
        assertEquals(0.0, s.getWidth());
        assertEquals(0.0, s.getHeight());
    }

    @Test
    void constructor_setsDimensions() {
        Size s = new Size(100, 50);
        assertEquals(100.0, s.getWidth());
        assertEquals(50.0, s.getHeight());
    }

    @Test
    void empty_isEmpty() {
        assertTrue(new Size(Double.NaN, Double.NaN).isEmpty());
        assertFalse(new Size(10, 10).isEmpty());
    }

    @Test
    void equals_comparesByValue() {
        assertEquals(new Size(10, 20), new Size(10, 20));
        assertNotEquals(new Size(10, 20), new Size(20, 10));
    }

    @Test
    void toString_matchesDotNetFormat() {
        assertEquals("0,0", new Size().toString());
        assertEquals("10,20", new Size(10, 20).toString());
        assertEquals("Empty", Size.EMPTY.toString());
    }
}
