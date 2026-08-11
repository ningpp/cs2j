package dotnet.drawing.swing;

import java.awt.Color;

import dotnet.drawing.swing.drawing2d.DashStyle;
import dotnet.drawing.swing.drawing2d.LineCap;

/**
 * System.Drawing.Pen compatibility class.
 */
public class Pen implements AutoCloseable {

    private Color color;
    private Brush brush;
    private float width;
    private float dashOffset;
    private float[] dashPattern;
    private DashStyle dashStyle = DashStyle.Solid;
    private LineCap endCap = LineCap.Flat;

    public Pen(Color color) {
        this.color = color;
        this.width = 1f;
    }

    public Pen(Color color, float width) {
        this.color = color;
        this.width = width;
    }

    public Pen(Brush brush, float width) {
        this.brush = brush;
        this.width = width;
        if (brush instanceof SolidBrush) {
            this.color = ((SolidBrush) brush).getColor();
        }
    }

    public Color getColor() {
        return color;
    }

    public void setColor(Color color) {
        this.color = color;
    }

    public Brush getBrush() {
        return brush;
    }

    public void setBrush(Brush brush) {
        this.brush = brush;
    }

    public float getWidth() {
        return width;
    }

    public void setWidth(float width) {
        this.width = width;
    }

    public float getDashOffset() {
        return dashOffset;
    }

    public void setDashOffset(float dashOffset) {
        this.dashOffset = dashOffset;
    }

    public float[] getDashPattern() {
        return dashPattern;
    }

    public void setDashPattern(float[] dashPattern) {
        this.dashPattern = dashPattern;
    }

    public DashStyle getDashStyle() {
        return dashStyle;
    }

    public void setDashStyle(DashStyle dashStyle) {
        this.dashStyle = dashStyle;
    }

    public LineCap getEndCap() {
        return endCap;
    }

    public void setEndCap(LineCap endCap) {
        this.endCap = endCap;
    }

    @Override
    public void close() {
    }
}