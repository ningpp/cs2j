package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class GeometryTest {

    @Test
    void rectangleGeometry_bounds() {
        RectangleGeometry g = new RectangleGeometry(new Rect(0, 0, 100, 50));
        assertEquals(new Rect(0, 0, 100, 50), g.getBounds());
        assertFalse(g.isEmpty());
    }

    @Test
    void rectangleGeometry_negativeSize_throwsLikeDotNet() {
        // .NET 6 WPF Rect ctor rejects negative dimensions (ArgumentException).
        assertThrows(IllegalArgumentException.class, () -> new Rect(0, 0, -5, 10));
    }

    @Test
    void ellipseGeometry_bounds() {
        EllipseGeometry g = new EllipseGeometry(new Point(10, 20), 5, 2);
        assertEquals(new Rect(5, 18, 10, 4), g.getBounds());
        assertTrue(g.mayHaveCurves());
    }

    @Test
    void fillContains_usesBounds() {
        RectangleGeometry g = new RectangleGeometry(new Rect(0, 0, 10, 10));
        assertTrue(g.fillContains(new Point(5, 5)));
        assertFalse(g.fillContains(new Point(50, 50)));
    }

    @Test
    void clone_independent() {
        RectangleGeometry g = new RectangleGeometry(new Rect(0, 0, 10, 10));
        RectangleGeometry c = g.clone();
        c.setRect(new Rect(100, 100, 1, 1));
        assertEquals(new Rect(0, 0, 10, 10), g.getRect());
    }
}
