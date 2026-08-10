package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Size;

import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.Media.StreamGeometryContext compatibility class.
 * Collects drawing commands into the owning {@link StreamGeometry}.
 */
public final class StreamGeometryContext implements AutoCloseable {

    private final StreamGeometry owner;
    private PathFigure currentFigure;
    private boolean open;

    StreamGeometryContext(StreamGeometry owner) {
        this.owner = owner;
        this.open = true;
    }

    public void beginFigure(Point startPoint, boolean isFilled, boolean isClosed) {
        checkOpen();
        currentFigure = new PathFigure();
        currentFigure.setStartPoint(startPoint == null ? new Point() : new Point(startPoint));
        currentFigure.setIsFilled(isFilled);
        currentFigure.setIsClosed(isClosed);
        owner.add(currentFigure);
    }

    public void lineTo(Point point, boolean isStroked, boolean isSmoothJoin) {
        checkOpen();
        ensureFigure();
        LineSegment s = new LineSegment(point == null ? new Point() : new Point(point), isStroked);
        s.setIsSmoothJoin(isSmoothJoin);
        currentFigure.getSegments().add(s);
    }

    public void bezierTo(Point point1, Point point2, Point point3, boolean isStroked, boolean isSmoothJoin) {
        checkOpen();
        ensureFigure();
        BezierSegment s = new BezierSegment(
                point1 == null ? new Point() : new Point(point1),
                point2 == null ? new Point() : new Point(point2),
                point3 == null ? new Point() : new Point(point3),
                isStroked);
        s.setIsSmoothJoin(isSmoothJoin);
        currentFigure.getSegments().add(s);
    }

    public void quadraticBezierTo(Point point1, Point point2, boolean isStroked, boolean isSmoothJoin) {
        checkOpen();
        ensureFigure();
        QuadraticBezierSegment s = new QuadraticBezierSegment(
                point1 == null ? new Point() : new Point(point1),
                point2 == null ? new Point() : new Point(point2),
                isStroked);
        s.setIsSmoothJoin(isSmoothJoin);
        currentFigure.getSegments().add(s);
    }

    public void polyLineTo(Iterable<Point> points, boolean isStroked, boolean isSmoothJoin) {
        checkOpen();
        ensureFigure();
        PolyLineSegment s = new PolyLineSegment(toPoints(points), isStroked);
        s.setIsSmoothJoin(isSmoothJoin);
        currentFigure.getSegments().add(s);
    }

    public void polyBezierTo(Iterable<Point> points, boolean isStroked, boolean isSmoothJoin) {
        checkOpen();
        ensureFigure();
        PolyBezierSegment s = new PolyBezierSegment(toPoints(points), isStroked);
        s.setIsSmoothJoin(isSmoothJoin);
        currentFigure.getSegments().add(s);
    }

    public void polyQuadraticBezierTo(Iterable<Point> points, boolean isStroked, boolean isSmoothJoin) {
        checkOpen();
        ensureFigure();
        PolyQuadraticBezierSegment s = new PolyQuadraticBezierSegment(toPoints(points), isStroked);
        s.setIsSmoothJoin(isSmoothJoin);
        currentFigure.getSegments().add(s);
    }

    public void arcTo(Point point, Size size, double rotationAngle, boolean isLargeArc,
                      SweepDirection sweepDirection, boolean isStroked, boolean isSmoothJoin) {
        checkOpen();
        ensureFigure();
        ArcSegment s = new ArcSegment(
                point == null ? new Point() : new Point(point),
                size == null ? new Size() : new Size(size.getWidth(), size.getHeight()),
                rotationAngle, isLargeArc, sweepDirection, isStroked);
        s.setIsSmoothJoin(isSmoothJoin);
        currentFigure.getSegments().add(s);
    }

    public void close() {
        open = false;
    }

    private void ensureFigure() {
        if (currentFigure == null) {
            beginFigure(new Point(), true, false);
        }
    }

    private void checkOpen() {
        if (!open) {
            throw new IllegalStateException("StreamGeometryContext is closed.");
        }
    }

    private static PointCollection toPoints(Iterable<Point> points) {
        PointCollection pc = new PointCollection();
        if (points != null) {
            for (Point p : points) {
                pc.add(p == null ? new Point() : new Point(p));
            }
        }
        return pc;
    }
}
