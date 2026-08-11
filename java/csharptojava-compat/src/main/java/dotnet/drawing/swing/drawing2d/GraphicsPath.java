package dotnet.drawing.swing.drawing2d;

import java.awt.geom.Arc2D;
import java.awt.geom.Path2D;

import dotnet.drawing.swing.PointF;

/**
 * System.Drawing.Drawing2D.GraphicsPath compatibility class.
 */
public class GraphicsPath implements AutoCloseable {

    private final Path2D.Float path = new Path2D.Float();

    public GraphicsPath() {
    }

    public void addLine(float x1, float y1, float x2, float y2) {
        path.moveTo(x1, y1);
        path.lineTo(x2, y2);
    }

    public void addLine(PointF pt1, PointF pt2) {
        if (pt1 != null && pt2 != null) {
            addLine(pt1.getX(), pt1.getY(), pt2.getX(), pt2.getY());
        }
    }

    public void addLines(dotnet.drawing.swing.Point[] points) {
        if (points == null || points.length == 0) {
            return;
        }
        dotnet.drawing.swing.Point first = points[0];
        if (first != null) {
            path.moveTo(first.getX(), first.getY());
        }
        for (int i = 1; i < points.length; i++) {
            dotnet.drawing.swing.Point pt = points[i];
            if (pt != null) {
                path.lineTo(pt.getX(), pt.getY());
            }
        }
    }

    public void addLines(PointF[] points) {
        if (points == null || points.length == 0) {
            return;
        }
        PointF first = points[0];
        if (first != null) {
            path.moveTo(first.getX(), first.getY());
        }
        for (int i = 1; i < points.length; i++) {
            PointF pt = points[i];
            if (pt != null) {
                path.lineTo(pt.getX(), pt.getY());
            }
        }
    }

    public void addBezier(PointF p1, PointF p2, PointF p3, PointF p4) {
        if (p1 != null && p2 != null && p3 != null && p4 != null) {
            addBezier(p1.getX(), p1.getY(), p2.getX(), p2.getY(), p3.getX(), p3.getY(), p4.getX(), p4.getY());
        }
    }

    public void addBezier(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4) {
        path.moveTo(x1, y1);
        path.curveTo(x2, y2, x3, y3, x4, y4);
    }

    public void addArc(float x, float y, float width, float height, float startAngle, float sweepAngle) {
        if (width <= 0f || height <= 0f) {
            return;
        }
        Arc2D.Float arc = new Arc2D.Float(x, y, width, height, startAngle, sweepAngle, Arc2D.OPEN);
        path.append(arc, true);
    }

    public void closeFigure() {
        path.closePath();
    }

    public void closeAllFigures() {
        path.closePath();
    }

    public Path2D getAwtPath() {
        return path;
    }

    @Override
    public void close() {
    }
}