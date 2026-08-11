package dotnet.drawing.swing;

import java.awt.BasicStroke;
import java.awt.Color;
import java.awt.Graphics2D;

import dotnet.drawing.swing.drawing2d.CompositingQuality;
import dotnet.drawing.swing.drawing2d.DashStyle;
import dotnet.drawing.swing.drawing2d.GraphicsPath;
import dotnet.drawing.swing.drawing2d.InterpolationMode;
import dotnet.drawing.swing.drawing2d.Matrix;
import dotnet.drawing.swing.drawing2d.SmoothingMode;
import dotnet.drawing.swing.text.TextRenderingHint;

/**
 * System.Drawing.Graphics compatibility class backed by java.awt.Graphics2D.
 */
public class Graphics implements AutoCloseable {

    private final Graphics2D g2d;

    private Region clip;
    private Matrix transform;
    private SmoothingMode smoothingMode;
    private CompositingQuality compositingQuality;
    private InterpolationMode interpolationMode;
    private TextRenderingHint textRenderingHint;

    public Graphics(Graphics2D g2d) {
        this.g2d = g2d;
    }

    public static Graphics fromImage(Image image) {
        if (image instanceof Bitmap) {
            java.awt.image.BufferedImage bufferedImage = ((Bitmap) image).getBufferedImage();
            if (bufferedImage != null) {
                return new Graphics(bufferedImage.createGraphics());
            }
        }
        return new Graphics(null);
    }

    @Override
    public void close() {
        if (g2d != null) {
            g2d.dispose();
        }
    }

    public long getHdc() {
        return 0L;
    }

    public void releaseHdc(long hdc) {
    }

    public void drawLine(Pen pen, int x1, int y1, int x2, int y2) {
        if (g2d == null) {
            return;
        }
        applyPen(pen);
        g2d.drawLine(x1, y1, x2, y2);
    }

    public void drawLine(Pen pen, float x1, float y1, float x2, float y2) {
        if (g2d == null) {
            return;
        }
        applyPen(pen);
        g2d.draw(new java.awt.geom.Line2D.Float(x1, y1, x2, y2));
    }

    public void drawLine(Pen pen, PointF pt1, PointF pt2) {
        if (g2d == null || pt1 == null || pt2 == null) {
            return;
        }
        applyPen(pen);
        g2d.draw(new java.awt.geom.Line2D.Float(pt1.getX(), pt1.getY(), pt2.getX(), pt2.getY()));
    }

    public void drawLines(Pen pen, PointF[] points) {
        if (g2d == null || points == null) {
            return;
        }
        applyPen(pen);
        for (int i = 1; i < points.length; i++) {
            PointF p0 = points[i - 1];
            PointF p1 = points[i];
            if (p0 != null && p1 != null) {
                g2d.draw(new java.awt.geom.Line2D.Float(p0.getX(), p0.getY(), p1.getX(), p1.getY()));
            }
        }
    }

    public void drawRectangle(Pen pen, int x, int y, int width, int height) {
        if (g2d == null) {
            return;
        }
        applyPen(pen);
        g2d.drawRect(x, y, width, height);
    }

    public void drawRectangle(Pen pen, float x, float y, float width, float height) {
        if (g2d == null) {
            return;
        }
        applyPen(pen);
        g2d.draw(new java.awt.geom.Rectangle2D.Float(x, y, width, height));
    }

    public void drawRectangle(Pen pen, Rectangle rect) {
        if (rect != null) {
            drawRectangle(pen, rect.getX(), rect.getY(), rect.getWidth(), rect.getHeight());
        }
    }

    public void drawEllipse(Pen pen, float x, float y, float width, float height) {
        if (g2d == null) {
            return;
        }
        applyPen(pen);
        g2d.draw(new java.awt.geom.Ellipse2D.Float(x, y, width, height));
    }

    public void drawEllipse(Pen pen, int x, int y, int width, int height) {
        drawEllipse(pen, (float) x, (float) y, (float) width, (float) height);
    }

    public void drawArc(Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle) {
        if (g2d == null) {
            return;
        }
        applyPen(pen);
        g2d.draw(new java.awt.geom.Arc2D.Float(x, y, width, height, startAngle, sweepAngle, java.awt.geom.Arc2D.OPEN));
    }

    public void drawBezier(Pen pen, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4) {
        if (g2d == null) {
            return;
        }
        applyPen(pen);
        java.awt.geom.Path2D.Float path = new java.awt.geom.Path2D.Float();
        path.moveTo(x1, y1);
        path.curveTo(x2, y2, x3, y3, x4, y4);
        g2d.draw(path);
    }

    public void drawPolygon(Pen pen, PointF[] points) {
        if (g2d == null || points == null || points.length == 0) {
            return;
        }
        applyPen(pen);
        java.awt.geom.Path2D.Float path = new java.awt.geom.Path2D.Float();
        path.moveTo(points[0].getX(), points[0].getY());
        for (int i = 1; i < points.length; i++) {
            PointF pt = points[i];
            if (pt != null) {
                path.lineTo(pt.getX(), pt.getY());
            }
        }
        path.closePath();
        g2d.draw(path);
    }

    public void drawPath(Pen pen, GraphicsPath path) {
        if (g2d == null || path == null) {
            return;
        }
        applyPen(pen);
        g2d.draw(path.getAwtPath());
    }

    public void drawString(String text, Font font, Brush brush, float x, float y) {
        drawString(text, font, brush, x, y, null);
    }

    public void drawString(String text, Font font, Brush brush, float x, float y, StringFormat format) {
        if (g2d == null || text == null) {
            return;
        }
        g2d.setColor(colorOf(brush));
        if (font != null && font.getAwtFont() != null) {
            g2d.setFont(font.getAwtFont());
        }
        g2d.drawString(text, x, y);
    }

    public void fillRectangle(Brush brush, int x, int y, int width, int height) {
        if (g2d == null) {
            return;
        }
        g2d.setColor(colorOf(brush));
        g2d.fillRect(x, y, width, height);
    }

    public void fillRectangle(Brush brush, float x, float y, float width, float height) {
        if (g2d == null) {
            return;
        }
        g2d.setColor(colorOf(brush));
        g2d.fill(new java.awt.geom.Rectangle2D.Float(x, y, width, height));
    }

    public void fillRectangle(Brush brush, RectangleF rect) {
        if (rect != null) {
            fillRectangle(brush, rect.getX(), rect.getY(), rect.getWidth(), rect.getHeight());
        }
    }

    public void fillRectangle(Brush brush, Rectangle rect) {
        if (rect != null) {
            fillRectangle(brush, rect.getX(), rect.getY(), rect.getWidth(), rect.getHeight());
        }
    }

    public void fillEllipse(Brush brush, float x, float y, float width, float height) {
        if (g2d == null) {
            return;
        }
        g2d.setColor(colorOf(brush));
        g2d.fill(new java.awt.geom.Ellipse2D.Float(x, y, width, height));
    }

    public void fillPolygon(Brush brush, PointF[] points) {
        if (g2d == null || points == null || points.length == 0) {
            return;
        }
        g2d.setColor(colorOf(brush));
        java.awt.geom.Path2D.Float path = new java.awt.geom.Path2D.Float();
        path.moveTo(points[0].getX(), points[0].getY());
        for (int i = 1; i < points.length; i++) {
            PointF pt = points[i];
            if (pt != null) {
                path.lineTo(pt.getX(), pt.getY());
            }
        }
        path.closePath();
        g2d.fill(path);
    }

    public void fillPath(Brush brush, GraphicsPath path) {
        if (g2d == null || path == null) {
            return;
        }
        g2d.setColor(colorOf(brush));
        g2d.fill(path.getAwtPath());
    }

    public SizeF measureString(String text, Font font, int maxWidth, StringFormat format) {
        if (text == null) {
            text = "";
        }
        if (g2d != null && font != null && font.getAwtFont() != null) {
            java.awt.geom.Rectangle2D bounds = g2d.getFontMetrics(font.getAwtFont()).getStringBounds(text, g2d);
            return new SizeF((float) bounds.getWidth(), (float) bounds.getHeight());
        }
        float size = font != null ? font.getSize() : 12f;
        return new SizeF(text.length() * 6f, size);
    }

    public SizeF measureString(String text, Font font) {
        return measureString(text, font, 0, null);
    }

    public Region getClip() {
        return clip;
    }

    public void setClip(Region clip) {
        this.clip = clip;
    }

    public Matrix getTransform() {
        return transform;
    }

    public void setTransform(Matrix transform) {
        this.transform = transform;
    }

    public SmoothingMode getSmoothingMode() {
        return smoothingMode;
    }

    public void setSmoothingMode(SmoothingMode smoothingMode) {
        this.smoothingMode = smoothingMode;
    }

    public CompositingQuality getCompositingQuality() {
        return compositingQuality;
    }

    public void setCompositingQuality(CompositingQuality compositingQuality) {
        this.compositingQuality = compositingQuality;
    }

    public InterpolationMode getInterpolationMode() {
        return interpolationMode;
    }

    public void setInterpolationMode(InterpolationMode interpolationMode) {
        this.interpolationMode = interpolationMode;
    }

    public TextRenderingHint getTextRenderingHint() {
        return textRenderingHint;
    }

    public void setTextRenderingHint(TextRenderingHint textRenderingHint) {
        this.textRenderingHint = textRenderingHint;
    }

    public float getDpiX() {
        return 96f;
    }

    public float getDpiY() {
        return 96f;
    }

    private void applyPen(Pen pen) {
        if (g2d == null || pen == null) {
            return;
        }
        Color color = pen.getColor();
        g2d.setColor(color != null ? color : Color.BLACK);
        float width = Math.max(0f, pen.getWidth());
        float[] dash = pen.getDashPattern();
        DashStyle dashStyle = pen.getDashStyle();
        if (dash != null && dash.length > 0 && dashStyle != null && dashStyle != DashStyle.Solid && allPositive(dash)) {
            float scale = width > 0f ? width : 1f;
            float[] scaled = new float[dash.length];
            for (int i = 0; i < dash.length; i++) {
                scaled[i] = dash[i] * scale;
            }
            g2d.setStroke(new BasicStroke(width, BasicStroke.CAP_BUTT, BasicStroke.JOIN_MITER, 10f, scaled,
                    Math.max(0f, pen.getDashOffset())));
        } else {
            g2d.setStroke(new BasicStroke(width));
        }
    }

    private static boolean allPositive(float[] dash) {
        for (float value : dash) {
            if (!(value > 0f)) {
                return false;
            }
        }
        return true;
    }

    private static Color colorOf(Brush brush) {
        if (brush instanceof SolidBrush) {
            Color color = ((SolidBrush) brush).getColor();
            if (color != null) {
                return color;
            }
        }
        return Color.BLACK;
    }
}