package dotnet.wpf.swing.media;

import dotnet.wpf.swing.FlowDirection;
import dotnet.wpf.swing.FontStretch;
import dotnet.wpf.swing.FontStyle;
import dotnet.wpf.swing.FontWeight;
import dotnet.wpf.swing.Point;

import java.awt.Font;
import java.awt.FontMetrics;
import java.awt.Graphics2D;
import java.awt.image.BufferedImage;

/**
 * System.Windows.Media.FormattedText compatibility class.
 * Measures text using the Swing font rendering pipeline so
 * Width/Height reflect the actual on-screen text size.
 */
public final class FormattedText {

    private String text;
    private final Object culture;
    private FlowDirection flowDirection;
    private Typeface typeface;
    private double emSize;
    private Brush foreground;
    private final Object numberSubstitution;
    private double pixelsPerDip;

    private double maxTextWidth = Double.POSITIVE_INFINITY;
    private double maxTextHeight = Double.POSITIVE_INFINITY;

    public FormattedText(String textToFormat, Object culture, FlowDirection flowDirection,
                         Typeface typeface, double emSize, Brush foreground) {
        this(textToFormat, culture, flowDirection, typeface, emSize, foreground, null, 0);
    }

    public FormattedText(String textToFormat, Object culture, FlowDirection flowDirection,
                         Typeface typeface, double emSize, Brush foreground, double pixelsPerDip) {
        this(textToFormat, culture, flowDirection, typeface, emSize, foreground, null, pixelsPerDip);
    }

    public FormattedText(String textToFormat, Object culture, FlowDirection flowDirection,
                         Typeface typeface, double emSize, Brush foreground,
                         Object numberSubstitution) {
        this(textToFormat, culture, flowDirection, typeface, emSize, foreground, numberSubstitution, 0);
    }

    public FormattedText(String textToFormat, Object culture, FlowDirection flowDirection,
                         Typeface typeface, double emSize, Brush foreground,
                         Object numberSubstitution, double pixelsPerDip) {
        this.text = textToFormat;
        this.culture = culture;
        this.flowDirection = flowDirection;
        this.typeface = typeface;
        this.emSize = emSize;
        this.foreground = foreground;
        this.numberSubstitution = numberSubstitution;
        this.pixelsPerDip = pixelsPerDip;
    }

    public String getText() {
        return text;
    }

    public double getPixelsPerDip() {
        return pixelsPerDip;
    }

    public void setPixelsPerDip(double value) {
        pixelsPerDip = value;
    }

    public FlowDirection getFlowDirection() {
        return flowDirection;
    }

    public void setFlowDirection(FlowDirection value) {
        flowDirection = value;
    }

    public double getMaxTextWidth() {
        return maxTextWidth;
    }

    public void setMaxTextWidth(double value) {
        maxTextWidth = value;
    }

    public double getMaxTextHeight() {
        return maxTextHeight;
    }

    public void setMaxTextHeight(double value) {
        maxTextHeight = value;
    }

    public double getWidth() {
        if (text == null || text.isEmpty()) {
            return 0;
        }
        double w = measure().stringWidth(text);
        if (Double.isFinite(maxTextWidth)) {
            w = Math.min(w, maxTextWidth);
        }
        return w;
    }

    public double getHeight() {
        if (text == null || text.isEmpty()) {
            return emSize;
        }
        double h = measure().getHeight();
        if (Double.isFinite(maxTextHeight)) {
            h = Math.min(h, maxTextHeight);
        }
        return h;
    }

    public double getWidthIncludingTrailingWhitespace() {
        return getWidth();
    }

    public double getBaseline() {
        return measure().getAscent();
    }

    public double getExtent() {
        return getHeight();
    }

    public double getLineHeight() {
        return getHeight();
    }

    public double getMinWidth() {
        return getWidth();
    }

    public void setFontSize(double emSize) {
        this.emSize = emSize;
    }

    public void setFontTypeface(Typeface value) {
        this.typeface = value;
    }

    public void setFontFamily(String fontFamily) {
        this.typeface = new Typeface(new FontFamily(fontFamily),
                this.typeface == null ? new FontStyle() : this.typeface.getStyle(),
                this.typeface == null ? dotnet.wpf.swing.FontWeights.Normal : this.typeface.getWeight(),
                this.typeface == null ? dotnet.wpf.swing.FontStretches.Normal : this.typeface.getStretch());
    }

    public void setForegroundBrush(Brush value) {
        this.foreground = value;
    }

    public Geometry buildGeometry(Point origin) {
        // Text geometry is approximated by an outline-rect geometry; consumers
        // mainly use the measured Width/Height.
        return new RectangleGeometry(new dotnet.wpf.swing.Rect(
                origin == null ? 0 : origin.getX(),
                origin == null ? 0 : origin.getY(),
                getWidth(), getHeight()));
    }

    public Geometry buildHighlightGeometry(Point origin) {
        return buildGeometry(origin);
    }

    private FontMetrics measure() {
        int style = Font.PLAIN;
        if (typeface != null && typeface.getStyle() != null
                && (FontStyle.ITALIC.equals(typeface.getStyle().getTag())
                || FontStyle.OBLIQUE.equals(typeface.getStyle().getTag()))) {
            style |= Font.ITALIC;
        }
        if (typeface != null && typeface.getWeight() != null
                && typeface.getWeight().getWeight() >= 700) {
            style |= Font.BOLD;
        }
        String name = typeface != null && typeface.getFontFamily() != null
                ? typeface.getFontFamily().getSource()
                : java.awt.Font.DIALOG;
        if (name == null || name.isEmpty()) {
            name = java.awt.Font.DIALOG;
        }
        Font font = new Font(name, style, Math.max(1, (int) Math.rint(emSize)));
        BufferedImage img = new BufferedImage(1, 1, BufferedImage.TYPE_INT_ARGB);
        Graphics2D g = img.createGraphics();
        try {
            if (pixelsPerDip > 0) {
                // scale graphics so the font maps to the requested DPI
                g.setFont(font.deriveFont((float) (emSize * (pixelsPerDip / 96.0))));
            } else {
                g.setFont(font);
            }
            return g.getFontMetrics();
        } finally {
            g.dispose();
        }
    }
}
