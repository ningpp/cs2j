package dotnet.wpf.swing;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class PointTest {

    @Test
    void defaultConstructor_isOrigin() {
        Point p = new Point();
        assertEquals(0.0, p.getX());
        assertEquals(0.0, p.getY());
    }

    @Test
    void constructor_setsCoordinates() {
        Point p = new Point(3.5, -2.0);
        assertEquals(3.5, p.getX());
        assertEquals(-2.0, p.getY());
    }

    @Test
    void setters_updateCoordinates() {
        Point p = new Point();
        p.setX(10);
        p.setY(20);
        assertEquals(10.0, p.getX());
        assertEquals(20.0, p.getY());
    }

    @Test
    void offset_movesPoint() {
        Point p = new Point(1, 2);
        p.offset(3, 4);
        assertEquals(new Point(4, 6), p);
    }

    @Test
    void equals_comparesByValue() {
        assertEquals(new Point(1, 2), new Point(1, 2));
        assertNotEquals(new Point(1, 2), new Point(2, 1));
        assertNotEquals(new Point(1, 2), null);
    }

    @Test
    void equals_staticHandlesNull() {
        assertTrue(Point.equals(null, null));
        assertFalse(Point.equals(new Point(), null));
        assertTrue(Point.equals(new Point(1, 1), new Point(1, 1)));
    }

    @Test
    void addSubtract_produceNewPoints() {
        assertEquals(new Point(4, 6), Point.add(new Point(1, 2), new Point(3, 4)));
        // .NET semantics: Point - Point => Vector (Length lives on Vector).
        assertEquals(new Vector(-2, -2), Point.subtract(new Point(1, 2), new Point(3, 4)));
    }

    @Test
    void hashCode_isConsistentWithEquals() {
        assertEquals(new Point(5, 6).hashCode(), new Point(5, 6).hashCode());
    }

    @Test
    void toString_matchesDotNetFormat() {
        // .NET invariant: 3.5,-2 ; 0,0 ; 1.5,2.25
        assertEquals("0,0", new Point().toString());
        assertEquals("3.5,-2", new Point(3.5, -2).toString());
        assertEquals("1.5,2.25", new Point(1.5, 2.25).toString());
    }
}
