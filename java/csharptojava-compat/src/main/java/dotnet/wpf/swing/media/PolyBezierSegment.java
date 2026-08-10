package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.PolyBezierSegment compatibility class.
 */
public final class PolyBezierSegment extends PathSegment {

    private PointCollection points;

    public PolyBezierSegment() {
        this(new PointCollection(), false);
    }

    public PolyBezierSegment(Iterable<Point> points, boolean isStroked) {
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
    public PolyBezierSegment clone() {
        PolyBezierSegment c = new PolyBezierSegment(
                points == null ? null : new PointCollection(points), getIsStroked());
        c.setIsSmoothJoin(getIsSmoothJoin());
        return c;
    }
}
