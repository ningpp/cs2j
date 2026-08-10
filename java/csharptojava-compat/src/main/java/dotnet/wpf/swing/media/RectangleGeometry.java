package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Rect;

/**
 * System.Windows.Media.RectangleGeometry compatibility class.
 */
public final class RectangleGeometry extends Geometry {

    private Rect rect;
    private double radiusX;
    private double radiusY;

    public RectangleGeometry() {
        this(new Rect());
    }

    public RectangleGeometry(Rect rect) {
        this.rect = rect;
    }

    public RectangleGeometry(Rect rect, double radiusX, double radiusY) {
        this.rect = rect;
        this.radiusX = radiusX;
        this.radiusY = radiusY;
    }

    public RectangleGeometry(Rect rect, double radiusX, double radiusY, Transform transform) {
        this(rect, radiusX, radiusY);
        setTransform(transform);
    }

    public Rect getRect() {
        return rect;
    }

    public void setRect(Rect value) {
        rect = value;
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
        return rect;
    }

    @Override
    public boolean isEmpty() {
        return rect == null || rect.isEmpty()
                || rect.getWidth() <= 0 || rect.getHeight() <= 0;
    }

    @Override
    public boolean mayHaveCurves() {
        return radiusX > 0 && radiusY > 0;
    }

    @Override
    public RectangleGeometry clone() {
        RectangleGeometry c = new RectangleGeometry(
                rect == null ? null : new Rect(rect),
                radiusX, radiusY);
        c.setTransform(getTransform() == null ? null : getTransform().clone());
        return c;
    }

    @Override
    public String toString() {
        return "RectangleGeometry " + rect;
    }
}
