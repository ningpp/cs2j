package dotnet.wpf.swing.shapes;

import dotnet.wpf.swing.media.Geometry;

/**
 * System.Windows.Shapes.Path compatibility class.
 */
public final class Path extends Shape {

    private Geometry data;

    public Geometry getData() {
        return data;
    }

    public void setData(Geometry value) {
        data = value;
        setRenderedGeometry(data);
    }

    @Override
    public Geometry getDefiningGeometry() {
        return data;
    }
}
