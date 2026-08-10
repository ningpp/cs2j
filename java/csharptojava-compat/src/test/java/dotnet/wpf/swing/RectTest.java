package dotnet.wpf.swing;

import dotnet.wpf.swing.media.Matrix;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class RectTest {

    @Test
    void ctorFromXYWH() {
        Rect r = new Rect(10, 20, 100, 50);
        assertEquals(10.0, r.getX());
        assertEquals(20.0, r.getY());
        assertEquals(100.0, r.getWidth());
        assertEquals(50.0, r.getHeight());
        assertEquals(10.0, r.getLeft());
        assertEquals(20.0, r.getTop());
        assertEquals(110.0, r.getRight());
        assertEquals(70.0, r.getBottom());
    }

    @Test
    void ctorNegativeDimensions_throws() {
        // .NET 6 WPF: ArgumentException for negative width/height (verified against real runtime).
        assertThrows(IllegalArgumentException.class, () -> new Rect(10, 20, -100, -50));
        assertThrows(IllegalArgumentException.class, () -> new Rect(10, 20, 100, -50));
    }

    @Test
    void ctorFromTwoPoints() {
        Rect r = new Rect(new Point(0, 0), new Point(10, 20));
        assertEquals(new Rect(0, 0, 10, 20), r);
        // reverse order still yields the same rectangle
        Rect r2 = new Rect(new Point(10, 20), new Point(0, 0));
        assertEquals(r, r2);
    }

    @Test
    void ctorFromSizeAndLocationAndSize() {
        assertEquals(new Rect(0, 0, 50, 60), new Rect(new Size(50, 60)));
        assertEquals(new Rect(5, 6, 50, 60), new Rect(new Point(5, 6), new Size(50, 60)));
    }

    @Test
    void containsPoint() {
        Rect r = new Rect(0, 0, 10, 10);
        assertTrue(r.contains(new Point(0, 0)));
        assertTrue(r.contains(new Point(10, 10)));
        assertTrue(r.contains(5, 5));
        assertFalse(r.contains(new Point(11, 5)));
        assertFalse(r.contains(new Point(5, -1)));
    }

    @Test
    void containsRect() {
        Rect r = new Rect(0, 0, 20, 20);
        assertTrue(r.contains(new Rect(2, 2, 5, 5)));
        assertFalse(r.contains(new Rect(-1, 0, 5, 5)));
        assertFalse(r.contains(new Rect(0, 0, 30, 30)));
    }

    @Test
    void intersectsWith() {
        Rect a = new Rect(0, 0, 10, 10);
        assertTrue(a.intersectsWith(new Rect(5, 5, 10, 10)));
        assertTrue(a.intersectsWith(new Rect(9, 9, 5, 5)));
        assertFalse(a.intersectsWith(new Rect(11, 0, 5, 5)));
        // .NET WPF counts edge contact as intersecting (verified against real runtime).
        assertTrue(a.intersectsWith(new Rect(10, 10, 5, 5)));
    }

    @Test
    void union_rectangles() {
        Rect a = new Rect(0, 0, 10, 10);
        Rect b = new Rect(20, 30, 5, 5);
        assertEquals(new Rect(0, 0, 25, 35), Rect.union(a, b));
    }

    @Test
    void union_whenEmpty() {
        Rect a = Rect.EMPTY;
        Rect b = new Rect(1, 2, 3, 4);
        assertEquals(b, Rect.union(a, b));
    }

    @Test
    void intersect_rectangles() {
        Rect a = new Rect(0, 0, 20, 20);
        Rect b = new Rect(10, 10, 20, 20);
        assertEquals(new Rect(10, 10, 10, 10), Rect.intersect(a, b));
    }

    @Test
    void offset_movesRect() {
        Rect r = new Rect(1, 2, 10, 10);
        r.offset(100, 200);
        assertEquals(new Rect(101, 202, 10, 10), r);
    }

    @Test
    void inflate_behavior() {
        Rect r = new Rect(5, 5, 10, 10);
        r.inflate(2, 3);
        assertEquals(new Rect(3, 2, 14, 16), r);
    }

    @Test
    void transformByMatrix() {
        Rect r = new Rect(0, 0, 10, 10);
        Rect t = r.transform(new Matrix(2, 0, 0, 2, 5, 5));
        assertEquals(new Rect(5, 5, 20, 20), t);
    }

    @Test
    void transform_instanceMutatesLikeDotNet() {
        Rect r = new Rect(0, 0, 10, 10);
        Matrix m = new Matrix(2, 0, 0, 2, 5, 5);
        r.transform(m);
        assertEquals(new Rect(5, 5, 20, 20), r);
    }

    @Test
    void toString_roundTripsValues() {
        Rect r = new Rect(1, 2, 3, 4);
        // .NET invariant format
        assertEquals("1,2,3,4", r.toString());
        assertEquals("Empty", Rect.EMPTY.toString());
    }
}
