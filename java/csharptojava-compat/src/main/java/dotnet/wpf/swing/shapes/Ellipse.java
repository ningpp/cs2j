package dotnet.wpf.swing.shapes;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.media.EllipseGeometry;
import dotnet.wpf.swing.media.Geometry;

/**
 * System.Windows.Shapes.Ellipse compatibility class.
 */
public final class Ellipse extends Shape {

    @Override
    public Geometry getDefiningGeometry() {
        double w = getWidthForGeometry();
        double h = getHeightForGeometry();
        if (w <= 0 || h <= 0) {
            return new EllipseGeometry(new Point(0, 0), 0, 0);
        }
        return new EllipseGeometry(new Point(w / 2, h / 2), w / 2, h / 2);
    }
}
