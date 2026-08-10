package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;

/**
 * System.Windows.Media.GeneralTransform compatibility base class.
 */
public abstract class GeneralTransform {

    public Point transform(Point point) {
        if (point == null) return null;
        try {
            return transformImpl(point);
        } catch (RuntimeException ex) {
            return new Point(Double.NaN, Double.NaN);
        }
    }

    public Rect transformBounds(Rect rect) {
        Point tl = transform(rect.getTopLeft());
        Point tr = transform(rect.getTopRight());
        Point bl = transform(rect.getBottomLeft());
        Point br = transform(rect.getBottomRight());
        Rect result = new Rect(tl, tr);
        result.union(bl);
        result.union(br);
        return result;
    }

    public boolean tryTransform(Point inPoint, ObjectHolder result) {
        Point p = transform(inPoint);
        if (p == null || Double.isNaN(p.getX()) || Double.isNaN(p.getY())) {
            return false;
        }
        result.setValue(p);
        return true;
    }

    public abstract GeneralTransform getInverse();

    protected abstract Point transformImpl(Point point);

    /** Simple ref-like holder for out-parameters. */
    public static final class ObjectHolder {
        private Object value;
        public Object getValue() { return value; }
        public void setValue(Object value) { this.value = value; }
    }
}
