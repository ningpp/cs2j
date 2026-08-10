package dotnet.wpf.swing.shapes;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.media.Brush;
import dotnet.wpf.swing.media.DoubleCollection;
import dotnet.wpf.swing.media.Geometry;
import dotnet.wpf.swing.media.PenLineCap;
import dotnet.wpf.swing.media.Stretch;

/**
 * System.Windows.Shapes.Shape compatibility base class.
 */
public abstract class Shape extends FrameworkElement {

    private Brush fill;
    private Brush stroke;
    private double strokeThickness = 1;
    private DoubleCollection strokeDashArray;
    private PenLineCap strokeStartLineCap = PenLineCap.Flat;
    private PenLineCap strokeEndLineCap = PenLineCap.Flat;
    private Stretch stretch = Stretch.None;
    private Geometry renderedGeometry;

    public Brush getFill() {
        return fill;
    }

    public void setFill(Brush value) {
        fill = value;
    }

    public Brush getStroke() {
        return stroke;
    }

    public void setStroke(Brush value) {
        stroke = value;
    }

    public double getStrokeThickness() {
        return strokeThickness;
    }

    public void setStrokeThickness(double value) {
        strokeThickness = value;
    }

    public DoubleCollection getStrokeDashArray() {
        return strokeDashArray;
    }

    public void setStrokeDashArray(DoubleCollection value) {
        strokeDashArray = value;
    }

    public PenLineCap getStrokeStartLineCap() {
        return strokeStartLineCap;
    }

    public void setStrokeStartLineCap(PenLineCap value) {
        strokeStartLineCap = value;
    }

    public PenLineCap getStrokeEndLineCap() {
        return strokeEndLineCap;
    }

    public void setStrokeEndLineCap(PenLineCap value) {
        strokeEndLineCap = value;
    }

    public PenLineCap getStrokeDashCap() {
        return PenLineCap.Flat;
    }

    public Stretch getStretch() {
        return stretch;
    }

    public void setStretch(Stretch value) {
        stretch = value;
    }

    public Geometry getRenderedGeometry() {
        return renderedGeometry;
    }

    public void setRenderedGeometry(Geometry value) {
        renderedGeometry = value;
    }

    public abstract Geometry getDefiningGeometry();

    protected double getWidthForGeometry() {
        double w = getActualWidth();
        if (w > 0) return w;
        double explicitW = getWidth();
        return Double.isNaN(explicitW) ? 0 : explicitW;
    }

    protected double getHeightForGeometry() {
        double h = getActualHeight();
        if (h > 0) return h;
        double explicitH = getHeight();
        return Double.isNaN(explicitH) ? 0 : explicitH;
    }
}
