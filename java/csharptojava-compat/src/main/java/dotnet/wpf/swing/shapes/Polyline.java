package dotnet.wpf.swing.shapes;

import dotnet.wpf.swing.media.FillRule;
import dotnet.wpf.swing.media.Geometry;
import dotnet.wpf.swing.media.PointCollection;

/**
 * System.Windows.Shapes.Polyline compatibility class.
 */
public final class Polyline extends Shape {

    private PointCollection points = new PointCollection();
    private FillRule fillRule = FillRule.EvenOdd;

    public PointCollection getPoints() {
        return points;
    }

    public void setPoints(PointCollection value) {
        points = value;
    }

    public FillRule getFillRule() {
        return fillRule;
    }

    public void setFillRule(FillRule value) {
        fillRule = value;
    }

    @Override
    public Geometry getDefiningGeometry() {
        dotnet.wpf.swing.media.PathFigure f = new dotnet.wpf.swing.media.PathFigure();
        if (!points.isEmpty()) {
            f.setStartPoint(points.get(0));
            for (int i = 1; i < points.size(); i++) {
                f.getSegments().add(new dotnet.wpf.swing.media.LineSegment(points.get(i), true));
            }
        }
        dotnet.wpf.swing.media.PathGeometry pg = new dotnet.wpf.swing.media.PathGeometry();
        pg.setFillRule(fillRule);
        pg.getFigures().add(f);
        return pg;
    }
}
