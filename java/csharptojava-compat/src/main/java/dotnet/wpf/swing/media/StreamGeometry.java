package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;
import dotnet.wpf.swing.Size;

import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.Media.StreamGeometry compatibility class.
 * Records the drawing commands issued via {@link StreamGeometryContext}.
 */
public final class StreamGeometry extends Geometry {

    List<PathFigure> figures = new ArrayList<>();
    private FillRule fillRule = FillRule.EvenOdd;

    /**
     * Opens the geometry for writing (Swing-backed, no actual rendering here).
     */
    public StreamGeometryContext open() {
        return new StreamGeometryContext(this);
    }

    public FillRule getFillRule() {
        return fillRule;
    }

    public void setFillRule(FillRule value) {
        fillRule = value;
    }

    void add(PathFigure figure) {
        figures.add(figure);
    }

    @Override
    public Rect getBounds() {
        PathGeometry pg = new PathGeometry();
        pg.getFigures().addAll(figures);
        return pg.getBounds();
    }

    @Override
    public boolean isEmpty() {
        return figures.isEmpty();
    }

    @Override
    public boolean mayHaveCurves() {
        for (PathFigure f : figures) {
            for (PathSegment s : f.getSegments()) {
                if (s instanceof BezierSegment || s instanceof QuadraticBezierSegment
                        || s instanceof ArcSegment || s instanceof PolyBezierSegment
                        || s instanceof PolyQuadraticBezierSegment) {
                    return true;
                }
            }
        }
        return false;
    }

    /**
     * Converts the captured commands to a PathGeometry for rendering/hit-testing.
     */
    public PathGeometry toPathGeometry() {
        PathGeometry pg = new PathGeometry();
        pg.setFillRule(fillRule);
        pg.getFigures().addAll(figures);
        return pg;
    }

    @Override
    public StreamGeometry clone() {
        StreamGeometry s = new StreamGeometry();
        s.fillRule = fillRule;
        for (PathFigure f : figures) {
            s.figures.add(f.clone());
        }
        return s;
    }

    @Override
    public String toString() {
        return "StreamGeometry Figures=" + figures.size();
    }
}
