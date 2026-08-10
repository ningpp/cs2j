package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Size;

/**
 * System.Windows.Media.ArcSegment compatibility class.
 */
public final class ArcSegment extends PathSegment {

    private Point point;
    private Size size;
    private double rotationAngle;
    private boolean isLargeArc;
    private SweepDirection sweepDirection;

    public ArcSegment() {
        this(new Point(), new Size(), 0, false, SweepDirection.Clockwise, false);
    }

    public ArcSegment(Point point, Size size, double rotationAngle, boolean isLargeArc,
                      SweepDirection sweepDirection, boolean isStroked) {
        this.point = point;
        this.size = size;
        this.rotationAngle = rotationAngle;
        this.isLargeArc = isLargeArc;
        this.sweepDirection = sweepDirection;
        setIsStroked(isStroked);
    }

    public Point getPoint() { return point; }
    public void setPoint(Point value) { point = value; }

    public Size getSize() { return size; }
    public void setSize(Size value) { size = value; }

    public double getRotationAngle() { return rotationAngle; }
    public void setRotationAngle(double value) { rotationAngle = value; }

    public boolean getIsLargeArc() { return isLargeArc; }
    public void setIsLargeArc(boolean value) { isLargeArc = value; }

    public SweepDirection getSweepDirection() { return sweepDirection; }
    public void setSweepDirection(SweepDirection value) { sweepDirection = value; }

    @Override
    public ArcSegment clone() {
        ArcSegment c = new ArcSegment(
                point == null ? null : new Point(point),
                size == null ? null : new Size(size.getWidth(), size.getHeight()),
                rotationAngle, isLargeArc, sweepDirection, getIsStroked());
        c.setIsSmoothJoin(getIsSmoothJoin());
        return c;
    }
}
