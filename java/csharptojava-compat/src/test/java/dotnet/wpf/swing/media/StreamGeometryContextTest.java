package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Size;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class StreamGeometryContextTest {

    @Test
    void beginFigureLineTo_buildsFigure() {
        StreamGeometry sg = new StreamGeometry();
        try (StreamGeometryContext ctx = sg.open()) {
            ctx.beginFigure(new Point(0, 0), true, false);
            ctx.lineTo(new Point(10, 0), true, false);
            ctx.lineTo(new Point(10, 10), true, false);
        }
        assertEquals(1, sg.toPathGeometry().getFigures().size());
        assertEquals(2, sg.toPathGeometry().getFigures().get(0).getSegments().size());
    }

    @Test
    void bezierAndArc_bounds() {
        StreamGeometry sg = new StreamGeometry();
        try (StreamGeometryContext ctx = sg.open()) {
            ctx.beginFigure(new Point(0, 0), false, false);
            ctx.bezierTo(new Point(5, 0), new Point(5, 10), new Point(10, 10), true, false);
            ctx.arcTo(new Point(20, 10), new Size(5, 5), 0, false,
                    SweepDirection.Clockwise, true, false);
        }
        PathGeometry pg = sg.toPathGeometry();
        assertTrue(pg.mayHaveCurves());
        assertTrue(pg.getBounds().getWidth() > 0);
        assertTrue(pg.getBounds().getHeight() > 0);
    }

    @Test
    void polyLineTo_addsSegments() {
        StreamGeometry sg = new StreamGeometry();
        try (StreamGeometryContext ctx = sg.open()) {
            ctx.beginFigure(new Point(0, 0), true, false);
            ctx.polyLineTo(java.util.Arrays.asList(new Point(1, 1), new Point(2, 2)), true, false);
        }
        assertEquals(1, sg.toPathGeometry().getFigures().get(0).getSegments().size());
    }

    @Test
    void closedContext_rejectsCommands() {
        StreamGeometryContext ctx = new StreamGeometry().open();
        ctx.close();
        assertThrows(IllegalStateException.class, () -> ctx.lineTo(new Point(1, 1), true, false));
    }

    @Test
    void emptyStreamGeometry_isEmpty() {
        StreamGeometry sg = new StreamGeometry();
        try (StreamGeometryContext ctx = sg.open()) {
        }
        assertTrue(sg.isEmpty());
    }
}
