package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.QuadraticBezierSegment compatibility class.
 */
public final class QuadraticBezierSegment extends PathSegment {

    private Point point1;
    private Point point2;

    public QuadraticBezierSegment() {
        this(new Point(), new Point(), false);
    }

    public QuadraticBezierSegment(Point point1, Point point2, boolean isStroked) {
        this.point1 = point1;
        this.point2 = point2;
        setIsStroked(isStroked);
    }

    public Point getPoint1() { return point1; }
    public void setPoint1(Point value) { point1 = value; }

    public Point getPoint2() { return point2; }
    public void setPoint2(Point value) { point2 = value; }

    @Override
    public QuadraticBezierSegment clone() {
        QuadraticBezierSegment c = new QuadraticBezierSegment(
                point1 == null ? null : new Point(point1),
                point2 == null ? null : new Point(point2),
                getIsStroked());
        c.setIsSmoothJoin(getIsSmoothJoin());
        return c;
    }
}
