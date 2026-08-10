package dotnet.wpf.swing.media;

import java.util.Objects;

/**
 * System.Windows.Media.Color compatibility value type (ARGB, byte channels).
 */
public final class Color {

    private int a;
    private int r;
    private int g;
    private int b;

    public Color() {
    }

    public Color(int a, int r, int g, int b) {
        this.a = a;
        this.r = r;
        this.g = g;
        this.b = b;
    }

    public static Color fromArgb(int a, int r, int g, int b) {
        return new Color(a, r, g, b);
    }

    public static Color fromRgb(int r, int g, int b) {
        return new Color(255, r, g, b);
    }

    public int getA() { return a; }
    public void setA(int value) { a = value; }

    public int getR() { return r; }
    public void setR(int value) { r = value; }

    public int getG() { return g; }
    public void setG(int value) { g = value; }

    public int getB() { return b; }
    public void setB(int value) { b = value; }

    /**
     * Converts to an AWT color with clamping (Swing-backed rendering).
     */
    public java.awt.Color toAwtColor() {
        return new java.awt.Color(
                Math.max(0, Math.min(255, r)),
                Math.max(0, Math.min(255, g)),
                Math.max(0, Math.min(255, b)),
                Math.max(0, Math.min(255, a)));
    }

    public static Color fromAwtColor(java.awt.Color c) {
        return new Color(c.getAlpha(), c.getRed(), c.getGreen(), c.getBlue());
    }

    public static boolean equals(Color color1, Color color2) {
        if (color1 == null || color2 == null) return color1 == color2;
        return color1.equals(color2);
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Color)) return false;
        Color that = (Color) o;
        return a == that.a && r == that.r && g == that.g && b == that.b;
    }

    @Override
    public int hashCode() {
        return Objects.hash(a, r, g, b);
    }

    @Override
    public String toString() {
        // .NET format: #AARRGGBB uppercase hex.
        return String.format(java.util.Locale.ROOT, "#%02X%02X%02X%02X",
                a & 0xFF, r & 0xFF, g & 0xFF, b & 0xFF);
    }
}
