package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.PolyLineSegment compatibility class.
 */
public final class PolyLineSegment extends PathSegment {

    private PointCollection points;

    public PolyLineSegment() {
        this(new PointCollection(), false);
    }

    public PolyLineSegment(Iterable<Point> points, boolean isStroked) {
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
    public PolyLineSegment clone() {
        PolyLineSegment c = new PolyLineSegment(
                points == null ? null : new PointCollection(points), getIsStroked());
        c.setIsSmoothJoin(getIsSmoothJoin());
        return c;
    }
}
