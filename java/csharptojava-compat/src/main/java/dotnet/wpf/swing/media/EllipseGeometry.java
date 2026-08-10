package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;

/**
 * System.Windows.Media.EllipseGeometry compatibility class.
 */
public final class EllipseGeometry extends Geometry {

    private Point center;
    private double radiusX;
    private double radiusY;

    public EllipseGeometry() {
        this(new Point(), 0, 0);
    }

    public EllipseGeometry(Rect rect) {
        this(new Point(rect.getX() + rect.getWidth() / 2, rect.getY() + rect.getHeight() / 2),
                rect.getWidth() / 2, rect.getHeight() / 2);
    }

    public EllipseGeometry(Point center, double radiusX, double radiusY) {
        this.center = center;
        this.radiusX = radiusX;
        this.radiusY = radiusY;
    }

    public EllipseGeometry(Point center, double radiusX, double radiusY, Transform transform) {
        this(center, radiusX, radiusY);
        setTransform(transform);
    }

    public Point getCenter() {
        return center;
    }

    public void setCenter(Point value) {
        center = value;
    }

    public double getRadiusX() {
        return radiusX;
    }

    public void setRadiusX(double value) {
        radiusX = value;
    }

    public double getRadiusY() {
        return radiusY;
    }

    public void setRadiusY(double value) {
        radiusY = value;
    }

    @Override
    public Rect getBounds() {
        return new Rect(center.getX() - radiusX, center.getY() - radiusY,
                radiusX * 2, radiusY * 2);
    }

    @Override
    public boolean isEmpty() {
        return radiusX <= 0 || radiusY <= 0;
    }

    @Override
    public boolean mayHaveCurves() {
        return true;
    }

    @Override
    public EllipseGeometry clone() {
        EllipseGeometry c = new EllipseGeometry(
                center == null ? null : new Point(center),
                radiusX, radiusY);
        c.setTransform(getTransform() == null ? null : getTransform().clone());
        return c;
    }

    @Override
    public String toString() {
        return "EllipseGeometry Center=" + center + " Rx=" + radiusX + " Ry=" + radiusY;
    }
}
