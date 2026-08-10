package dotnet.wpf.swing.shapes;

import dotnet.wpf.swing.media.Geometry;
import dotnet.wpf.swing.media.LineSegment;
import dotnet.wpf.swing.media.PathFigure;
import dotnet.wpf.swing.media.PathGeometry;
import dotnet.wpf.swing.media.StreamGeometry;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class ShapesTest {

    @Test
    void path_data() {
        Path p = new Path();
        assertNull(p.getData());
        StreamGeometry sg = new StreamGeometry();
        try (dotnet.wpf.swing.media.StreamGeometryContext ctx = sg.open()) {
            ctx.beginFigure(new dotnet.wpf.swing.Point(0, 0), true, false);
            ctx.lineTo(new dotnet.wpf.swing.Point(10, 10), true, false);
        }
        p.setData(sg);
        assertSame(sg, p.getData());
        assertSame(sg, p.getRenderedGeometry());
    }

    @Test
    void path_strokeProps() {
        Path p = new Path();
        assertEquals(1.0, p.getStrokeThickness());
        p.setStrokeThickness(3.5);
        assertEquals(3.5, p.getStrokeThickness());
        p.setStroke(dotnet.wpf.swing.media.Brushes.Red);
        assertEquals(dotnet.wpf.swing.media.Brushes.Red, p.getStroke());
    }

    @Test
    void rectangle_definingGeometry() {
        Rectangle r = new Rectangle();
        r.setWidth(100);
        r.setHeight(50);
        Geometry g = r.getDefiningGeometry();
        assertFalse(g.isEmpty());
        assertEquals(new dotnet.wpf.swing.Rect(0, 0, 100, 50), g.getBounds());
    }

    @Test
    void ellipse_definingGeometry() {
        Ellipse e = new Ellipse();
        e.setWidth(10);
        e.setHeight(8);
        Geometry g = e.getDefiningGeometry();
        assertFalse(g.isEmpty());
        assertEquals(new dotnet.wpf.swing.Rect(0, 0, 10, 8), g.getBounds());
    }

    @Test
    void line_definingGeometry() {
        Line l = new Line();
        l.setX1(0);
        l.setY1(0);
        l.setX2(10);
        l.setY2(5);
        Geometry g = l.getDefiningGeometry();
        assertEquals(new dotnet.wpf.swing.Rect(0, 0, 10, 5), g.getBounds());
    }

    @Test
    void polyline_definingGeometry() {
        Polyline pl = new Polyline();
        pl.getPoints().add(new dotnet.wpf.swing.Point(0, 0));
        pl.getPoints().add(new dotnet.wpf.swing.Point(10, 0));
        pl.getPoints().add(new dotnet.wpf.swing.Point(10, 8));
        Geometry g = pl.getDefiningGeometry();
        assertEquals(new dotnet.wpf.swing.Rect(0, 0, 10, 8), g.getBounds());
    }
}
