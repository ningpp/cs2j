package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Rect;

import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.Media.DrawingContext compatibility.
 * Records a list of drawing operations (currently only rectangles needed for
 * the GraphmapsWpfControl viewer's offscreen rendering).
 */
public final class DrawingContext implements AutoCloseable {

    private boolean closed;
    private final List<Object> operations = new ArrayList<>();

    public void drawRectangle(Brush brush, Object pen, Rect rectangle) {
        checkOpen();
        operations.add(new RectOp(brush, pen, rectangle));
    }

    public void drawLine(Object pen, dotnet.wpf.swing.Point point0, dotnet.wpf.swing.Point point1) {
        checkOpen();
        operations.add(new LineOp(pen, point0, point1));
    }

    public void close() {
        closed = true;
    }

    public List<Object> getOperations() {
        return operations;
    }

    private void checkOpen() {
        if (closed) {
            throw new IllegalStateException("DrawingContext is closed.");
        }
    }

    /** A recorded rectangle draw operation. */
    public static final class RectOp {
        public final Brush brush;
        public final Object pen;
        public final Rect rect;

        RectOp(Brush brush, Object pen, Rect rect) {
            this.brush = brush;
            this.pen = pen;
            this.rect = rect;
        }
    }

    /** A recorded line draw operation. */
    public static final class LineOp {
        public final Object pen;
        public final dotnet.wpf.swing.Point point0;
        public final dotnet.wpf.swing.Point point1;

        LineOp(Object pen, dotnet.wpf.swing.Point point0, dotnet.wpf.swing.Point point1) {
            this.pen = pen;
            this.point0 = point0;
            this.point1 = point1;
        }
    }
}
