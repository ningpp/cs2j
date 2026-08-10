package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Rect;

/**
 * System.Windows.Media.Geometry compatibility base class.
 * WPF geometries are abstract shapes; here we carry a bounding box and an
 * outline built from PathFigures so shapes/hit-testing can rely on it.
 */
public abstract class Geometry {

    public static final Geometry EMPTY = new Geometry() {
        @Override
        public Rect getBounds() {
            return Rect.EMPTY;
        }

        @Override
        public boolean isEmpty() {
            return true;
        }

        @Override
        public boolean mayHaveCurves() {
            return false;
        }

        @Override
        public String toString() {
            return "Geometry.Empty";
        }
    };

    private Transform transform;

    public Rect getBounds() {
        return Rect.EMPTY;
    }

    public boolean isEmpty() {
        return false;
    }

    public boolean mayHaveCurves() {
        return true;
    }

    public Transform getTransform() {
        return transform;
    }

    public void setTransform(Transform value) {
        transform = value;
    }

    public boolean fillContains(dotnet.wpf.swing.Point hitPoint) {
        return getBounds().contains(hitPoint);
    }

    public Geometry clone() {
        try {
            Geometry c = (Geometry) super.clone();
            c.transform = transform;
            return c;
        } catch (CloneNotSupportedException e) {
            throw new RuntimeException(e);
        }
    }

    @Override
    public String toString() {
        return "Geometry";
    }
}
