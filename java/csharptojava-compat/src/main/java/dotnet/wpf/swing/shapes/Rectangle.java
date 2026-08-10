package dotnet.wpf.swing.shapes;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;
import dotnet.wpf.swing.media.Geometry;
import dotnet.wpf.swing.media.RectangleGeometry;

/**
 * System.Windows.Shapes.Rectangle compatibility class.
 */
public final class Rectangle extends Shape {

    private double radiusX;
    private double radiusY;

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
    public Geometry getDefiningGeometry() {
        double w = getWidthForGeometry();
        double h = getHeightForGeometry();
        return new RectangleGeometry(new Rect(0, 0, w, h), radiusX, radiusY);
    }
}
