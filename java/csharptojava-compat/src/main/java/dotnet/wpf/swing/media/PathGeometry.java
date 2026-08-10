package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;

/**
 * System.Windows.Media.PathGeometry compatibility class.
 */
public final class PathGeometry extends Geometry {

    private PathFigureCollection figures = new PathFigureCollection();
    private FillRule fillRule = FillRule.EvenOdd;

    public PathGeometry() {
    }

    public PathGeometry(Iterable<PathFigure> figures) {
        this.figures = figures instanceof PathFigureCollection
                ? (PathFigureCollection) figures
                : new PathFigureCollection(figures);
    }

    public PathGeometry(Iterable<PathFigure> figures, FillRule fillRule, Transform transform) {
        this(figures);
        this.fillRule = fillRule;
        setTransform(transform);
    }

    public PathFigureCollection getFigures() {
        return figures;
    }

    public void setFigures(PathFigureCollection value) {
        figures = value;
    }

    public FillRule getFillRule() {
        return fillRule;
    }

    public void setFillRule(FillRule value) {
        fillRule = value;
    }

    public void addGeometry(Geometry geometry) {
        if (geometry instanceof PathGeometry) {
            figures.addAll(((PathGeometry) geometry).figures);
        } else if (geometry instanceof RectangleGeometry) {
            RectangleGeometry rg = (RectangleGeometry) geometry;
            Rect r = rg.getRect();
            figures.add(rectFigure(r.getX(), r.getY(), r.getWidth(), r.getHeight()));
        } else if (geometry instanceof EllipseGeometry) {
            EllipseGeometry eg = (EllipseGeometry) geometry;
            Point c = eg.getCenter();
            figures.add(ellipseFigure(c.getX(), c.getY(), eg.getRadiusX(), eg.getRadiusY()));
        }
    }

    public void clear() {
        figures.clear();
    }

    public static PathGeometry createFromGeometry(Geometry geometry) {
        if (geometry instanceof PathGeometry) {
            return ((PathGeometry) geometry).clone();
        }
        PathGeometry pg = new PathGeometry();
        pg.addGeometry(geometry);
        return pg;
    }

    public void getPointAtFractionLength(double progress, PointHolder point, PointHolder tangent) {
        PathFigure first = figures.isEmpty() ? null : figures.get(0);
        if (first == null) {
            point.setValue(new Point());
            tangent.setValue(new Point(1, 0));
            return;
        }
        Point start = first.getStartPoint();
        point.setValue(start);
        tangent.setValue(new Point(1, 0));
    }

    @Override
    public Rect getBounds() {
        Rect bounds = Rect.EMPTY;
        for (PathFigure f : figures) {
            Point p = f.getStartPoint();
            Rect r = new Rect(p, new dotnet.wpf.swing.Size(0, 0));
            for (PathSegment s : f.getSegments()) {
                if (s instanceof LineSegment) {
                    r.union(((LineSegment) s).getPoint());
                } else if (s instanceof BezierSegment) {
                    BezierSegment b = (BezierSegment) s;
                    r.union(b.getPoint1());
                    r.union(b.getPoint2());
                    r.union(b.getPoint3());
                } else if (s instanceof QuadraticBezierSegment) {
                    QuadraticBezierSegment q = (QuadraticBezierSegment) s;
                    r.union(q.getPoint1());
                    r.union(q.getPoint2());
                } else if (s instanceof ArcSegment) {
                    r.union(((ArcSegment) s).getPoint());
                } else if (s instanceof PolyLineSegment) {
                    for (Point pt : ((PolyLineSegment) s).getPoints()) {
                        r.union(pt);
                    }
                } else if (s instanceof PolyBezierSegment) {
                    for (Point pt : ((PolyBezierSegment) s).getPoints()) {
                        r.union(pt);
                    }
                } else if (s instanceof PolyQuadraticBezierSegment) {
                    for (Point pt : ((PolyQuadraticBezierSegment) s).getPoints()) {
                        r.union(pt);
                    }
                }
            }
            bounds = bounds.isEmpty() ? r : Rect.union(bounds, r);
        }
        return bounds.isEmpty() ? Rect.EMPTY : bounds;
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

    private static PathFigure rectFigure(double x, double y, double w, double h) {
        PathFigure f = new PathFigure();
        f.setStartPoint(new Point(x, y));
        f.getSegments().add(new LineSegment(new Point(x + w, y), true));
        f.getSegments().add(new LineSegment(new Point(x + w, y + h), true));
        f.getSegments().add(new LineSegment(new Point(x, y + h), true));
        f.setIsClosed(true);
        return f;
    }

    private static PathFigure ellipseFigure(double cx, double cy, double rx, double ry) {
        PathFigure f = new PathFigure();
        f.setStartPoint(new Point(cx - rx, cy));
        f.getSegments().add(new BezierSegment(
                new Point(cx - rx, cy + ry * 0.5522847498),
                new Point(cx - rx * 0.5522847498, cy + ry),
                new Point(cx, cy + ry), true));
        f.getSegments().add(new BezierSegment(
                new Point(cx + rx * 0.5522847498, cy + ry),
                new Point(cx + rx, cy + ry * 0.5522847498),
                new Point(cx + rx, cy), true));
        f.getSegments().add(new BezierSegment(
                new Point(cx + rx, cy - ry * 0.5522847498),
                new Point(cx + rx * 0.5522847498, cy - ry),
                new Point(cx, cy - ry), true));
        f.getSegments().add(new BezierSegment(
                new Point(cx - rx * 0.5522847498, cy - ry),
                new Point(cx - rx, cy - ry * 0.5522847498),
                new Point(cx - rx, cy), true));
        f.setIsClosed(true);
        return f;
    }

    @Override
    public PathGeometry clone() {
        PathGeometry c = new PathGeometry();
        c.figures = new PathFigureCollection();
        for (PathFigure f : figures) {
            c.figures.add(f.clone());
        }
        c.fillRule = fillRule;
        c.setTransform(getTransform() == null ? null : getTransform().clone());
        return c;
    }

    @Override
    public String toString() {
        return "PathGeometry Figures=" + figures.size();
    }

    /** Holder for out parameters. */
    public static final class PointHolder {
        private Point value;
        public Point getValue() { return value; }
        public void setValue(Point v) { value = v; }
    }
}
