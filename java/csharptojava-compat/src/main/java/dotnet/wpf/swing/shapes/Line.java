package dotnet.wpf.swing.shapes;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;
import dotnet.wpf.swing.media.Geometry;
import dotnet.wpf.swing.media.LineSegment;
import dotnet.wpf.swing.media.PathFigure;
import dotnet.wpf.swing.media.PathGeometry;

/**
 * System.Windows.Shapes.Line compatibility class.
 */
public final class Line extends Shape {

    private double x1;
    private double y1;
    private double x2;
    private double y2;

    public double getX1() { return x1; }
    public void setX1(double value) { x1 = value; }

    public double getY1() { return y1; }
    public void setY1(double value) { y1 = value; }

    public double getX2() { return x2; }
    public void setX2(double value) { x2 = value; }

    public double getY2() { return y2; }
    public void setY2(double value) { y2 = value; }

    @Override
    public Geometry getDefiningGeometry() {
        PathFigure f = new PathFigure();
        f.setStartPoint(new Point(x1, y1));
        f.getSegments().add(new LineSegment(new Point(x2, y2), true));
        PathGeometry pg = new PathGeometry();
        pg.getFigures().add(f);
        return pg;
    }
}
