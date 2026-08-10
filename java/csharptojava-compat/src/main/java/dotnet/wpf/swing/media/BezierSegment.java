package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.BezierSegment compatibility class (cubic Bezier).
 */
public final class BezierSegment extends PathSegment {

    private Point point1;
    private Point point2;
    private Point point3;

    public BezierSegment() {
        this(new Point(), new Point(), new Point(), false);
    }

    public BezierSegment(Point point1, Point point2, Point point3, boolean isStroked) {
        this.point1 = point1;
        this.point2 = point2;
        this.point3 = point3;
        setIsStroked(isStroked);
    }

    public Point getPoint1() { return point1; }
    public void setPoint1(Point value) { point1 = value; }

    public Point getPoint2() { return point2; }
    public void setPoint2(Point value) { point2 = value; }

    public Point getPoint3() { return point3; }
    public void setPoint3(Point value) { point3 = value; }

    @Override
    public BezierSegment clone() {
        BezierSegment c = new BezierSegment(
                point1 == null ? null : new Point(point1),
                point2 == null ? null : new Point(point2),
                point3 == null ? null : new Point(point3),
                getIsStroked());
        c.setIsSmoothJoin(getIsSmoothJoin());
        return c;
    }
}
