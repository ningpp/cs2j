package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.PolyQuadraticBezierSegment compatibility class.
 */
public final class PolyQuadraticBezierSegment extends PathSegment {

    private PointCollection points;

    public PolyQuadraticBezierSegment() {
        this(new PointCollection(), false);
    }

    public PolyQuadraticBezierSegment(Iterable<Point> points, boolean isStroked) {
        this.points = points instanceof PointCollection
                ? (PointCollection) points
                : new PointCollection(points);
        setIsStroked(isStroked);
    }

    public PointCollection getPoints() {
        return points;
    }

    public void setPoints(PointCollection value) {
        points = value;
    }

    @Override
    public PolyQuadraticBezierSegment clone() {
        PolyQuadraticBezierSegment c = new PolyQuadraticBezierSegment(
                points == null ? null : new PointCollection(points), getIsStroked());
        c.setIsSmoothJoin(getIsSmoothJoin());
        return c;
    }
}
