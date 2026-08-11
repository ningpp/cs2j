package dotnet.drawing.swing;

import java.awt.Shape;

import dotnet.drawing.swing.drawing2d.GraphicsPath;

/**
 * System.Drawing.Region compatibility class.
 */
public class Region {

    private final Shape shape;

    public Region(GraphicsPath path) {
        this.shape = path != null ? path.getAwtPath() : null;
    }

    public Region(RectangleF rect) {
        this.shape = rect == null
                ? null
                : new java.awt.geom.Rectangle2D.Float(rect.getX(), rect.getY(), rect.getWidth(), rect.getHeight());
    }

    public Shape getShape() {
        return shape;
    }
}