package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.LineSegment compatibility class.
 */
public final class LineSegment extends PathSegment {

    private Point point;

    public LineSegment() {
        this(new Point(), false);
    }

    public LineSegment(Point point, boolean isStroked) {
        this.point = point;
        setIsStroked(isStroked);
    }

    public Point getPoint() {
        return point;
    }

    public void setPoint(Point value) {
        point = value;
    }

    @Override
    public LineSegment clone() {
        LineSegment c = new LineSegment(point == null ? null : new Point(point), getIsStroked());
        c.setIsSmoothJoin(getIsSmoothJoin());
        return c;
    }
}
