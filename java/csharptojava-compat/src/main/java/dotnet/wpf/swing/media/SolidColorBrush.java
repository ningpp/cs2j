package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.SolidColorBrush compatibility class.
 */
public final class SolidColorBrush extends Brush {

    private Color color;

    public SolidColorBrush() {
        this(new Color());
    }

    public SolidColorBrush(Color color) {
        this.color = color;
    }

    public Color getColor() {
        return color;
    }

    public void setColor(Color value) {
        this.color = value;
    }

    @Override
    public java.awt.Color toAwtColor() {
        java.awt.Color c = color == null ? new Color().toAwtColor() : color.toAwtColor();
        if (opacity() < 1.0) {
            int alpha = (int) Math.round(c.getAlpha() * opacity());
            return new java.awt.Color(c.getRed(), c.getGreen(), c.getBlue(), Math.max(0, Math.min(255, alpha)));
        }
        return c;
    }

    private double opacity() {
        return getOpacity();
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof SolidColorBrush)) return false;
        SolidColorBrush that = (SolidColorBrush) o;
        return getOpacity() == that.getOpacity()
                && (color == null ? that.color == null : color.equals(that.color));
    }

    @Override
    public int hashCode() {
        return color == null ? 0 : color.hashCode();
    }

    @Override
    public SolidColorBrush clone() {
        SolidColorBrush b = new SolidColorBrush(color == null ? null : new Color(
                color.getA(), color.getR(), color.getG(), color.getB()));
        b.setOpacity(getOpacity());
        b.setTransform(getTransform());
        b.setRelativeTransform(getRelativeTransform());
        return b;
    }

    @Override
    public String toString() {
        return "SolidColorBrush: " + color;
    }
}
