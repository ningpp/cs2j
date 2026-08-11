package dotnet.drawing.swing;

import java.awt.Color;

/**
 * System.Drawing.SolidBrush compatibility class.
 */
public class SolidBrush implements Brush, AutoCloseable {

    private Color color;

    public SolidBrush(Color color) {
        this.color = color;
    }

    public Color getColor() {
        return color;
    }

    public void setColor(Color color) {
        this.color = color;
    }

    @Override
    public void close() {
    }
}