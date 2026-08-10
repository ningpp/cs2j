package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class PathGeometryTest {

    @Test
    void addLineSegments_bounds() {
        PathGeometry pg = new PathGeometry();
        PathFigure f = new PathFigure();
        f.setStartPoint(new Point(0, 0));
        f.getSegments().add(new LineSegment(new Point(10, 0), true));
        f.getSegments().add(new LineSegment(new Point(10, 20), true));
        pg.getFigures().add(f);
        assertEquals(new Rect(0, 0, 10, 20), pg.getBounds());
    }

    @Test
    void addEllipseGeometry_bounds() {
        PathGeometry pg = new PathGeometry();
        pg.addGeometry(new EllipseGeometry(new Point(5, 5), 5, 3));
        assertEquals(new Rect(0, 2, 10, 6), pg.getBounds());
    }

    @Test
    void createFromRectangleGeometry() {
        PathGeometry pg = PathGeometry.createFromGeometry(new RectangleGeometry(new Rect(1, 2, 10, 20)));
        assertEquals(1, pg.getFigures().size());
        assertEquals(new Rect(1, 2, 10, 20), pg.getBounds());
    }

    @Test
    void emptyGeometry_isEmpty() {
        assertTrue(new PathGeometry().isEmpty());
    }

    @Test
    void clone_isDeepIndependent() {
        PathGeometry pg = new PathGeometry();
        PathFigure f = new PathFigure();
        f.setStartPoint(new Point(0, 0));
        pg.getFigures().add(f);
        PathGeometry c = pg.clone();
        c.getFigures().clear();
        assertEquals(1, pg.getFigures().size());
    }
}
