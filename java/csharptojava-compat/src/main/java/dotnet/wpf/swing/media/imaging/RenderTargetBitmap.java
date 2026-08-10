package dotnet.wpf.swing.media.imaging;

import dotnet.wpf.swing.media.BitmapSource;
import dotnet.wpf.swing.media.Visual;

import java.io.File;
import java.io.IOException;

/**
 * System.Windows.Media.Imaging.RenderTargetBitmap compatibility class.
 * Swing-backed: renders a Visual hierarchy into an AWT BufferedImage by capturing
 * rectangular area + transform. Basic implementation: fills an offscreen image and
 * records the visual so it can be rendered.
 */
public final class RenderTargetBitmap extends BitmapSource {

    private final Visual visualToRender;

    public RenderTargetBitmap(int pixelWidth, int pixelHeight, double dpiX, double dpiY,
                              dotnet.wpf.swing.media.PixelFormat pixelFormat) {
        super(new java.awt.image.BufferedImage(Math.max(1, pixelWidth), Math.max(1, pixelHeight),
                java.awt.image.BufferedImage.TYPE_INT_ARGB), dpiX, dpiY);
        this.visualToRender = null;
    }

    public void clear() {
        java.awt.Graphics2D g = getImage().createGraphics();
        try {
            g.setComposite(java.awt.AlphaComposite.Clear);
            g.fillRect(0, 0, getImage().getWidth(), getImage().getHeight());
            g.setComposite(java.awt.AlphaComposite.SrcOver);
        } finally {
            g.dispose();
        }
    }

    public void render(Visual visual) {
        if (visual == null) {
            return;
        }
        java.awt.Graphics2D g = getImage().createGraphics();
        try {
            paintVisual(g, visual);
        } finally {
            g.dispose();
        }
    }

    private void paintVisual(java.awt.Graphics2D g, Visual visual) {
        if (visual instanceof dotnet.wpf.swing.shapes.Shape shape) {
            paintShape(g, shape);
        }
        if (visual instanceof dotnet.wpf.swing.controls.Panel panel) {
            for (dotnet.wpf.swing.UIElement child : panel.getChildren()) {
                paintVisual(g, child);
            }
        }
        if (visual instanceof dotnet.wpf.swing.controls.TextBlock tb) {
            g.setColor(java.awt.Color.BLACK);
            g.drawString(tb.getText() == null ? "" : tb.getText(),
                    (float) (tb.getActualWidth() < 0 ? 0 : tb.getActualWidth() - textWidth(tb)),
                    (float) tb.getActualHeight());
        }
    }

    private void paintShape(java.awt.Graphics2D g, dotnet.wpf.swing.shapes.Shape shape) {
        double w = shape.getActualWidth() > 0 ? shape.getActualWidth()
                : (Double.isNaN(shape.getWidth()) ? 0 : shape.getWidth());
        double h = shape.getActualHeight() > 0 ? shape.getActualHeight()
                : (Double.isNaN(shape.getHeight()) ? 0 : shape.getHeight());
        if (shape.getFill() != null) {
            g.setColor(shape.getFill().toAwtColor());
            g.fill(new java.awt.geom.RoundRectangle2D.Double(0, 0, w, h,
                    shape instanceof dotnet.wpf.swing.shapes.Rectangle
                            ? ((dotnet.wpf.swing.shapes.Rectangle) shape).getRadiusX() : 0,
                    shape instanceof dotnet.wpf.swing.shapes.Rectangle
                            ? ((dotnet.wpf.swing.shapes.Rectangle) shape).getRadiusY() : 0));
        }
        if (shape.getStroke() != null && shape.getStrokeThickness() > 0) {
            g.setColor(shape.getStroke().toAwtColor());
            java.awt.Stroke old = g.getStroke();
            g.setStroke(new java.awt.BasicStroke((float) shape.getStrokeThickness()));
            g.draw(new java.awt.geom.RoundRectangle2D.Double(0, 0, w, h, 0, 0));
            g.setStroke(old);
        }
    }

    private static double textWidth(dotnet.wpf.swing.controls.TextBlock tb) {
        java.awt.FontMetrics fm = textMetrics(tb);
        return fm == null || tb.getText() == null ? 0 : fm.stringWidth(tb.getText());
    }

    private static java.awt.FontMetrics textMetrics(dotnet.wpf.swing.controls.TextBlock tb) {
        try {
            java.awt.image.BufferedImage img = new java.awt.image.BufferedImage(1, 1,
                    java.awt.image.BufferedImage.TYPE_INT_ARGB);
            java.awt.Graphics2D g = img.createGraphics();
            try {
                g.setFont(new java.awt.Font(java.awt.Font.DIALOG, java.awt.Font.PLAIN,
                        (int) tb.getFontSize()));
                return g.getFontMetrics();
            } finally {
                g.dispose();
            }
        } catch (Throwable t) {
            return null;
        }
    }
}
