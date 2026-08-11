package dotnet.drawing.swing;

import java.awt.Color;

/**
 * System.Drawing.Brushes compatibility class.
 */
public final class Brushes {

    public static final SolidBrush Black = new SolidBrush(Color.BLACK);
    public static final SolidBrush Blue = new SolidBrush(Color.BLUE);
    public static final SolidBrush Brown = new SolidBrush(new Color(165, 42, 42));
    public static final SolidBrush Gray = new SolidBrush(Color.GRAY);
    public static final SolidBrush LightGray = new SolidBrush(Color.LIGHT_GRAY);
    public static final SolidBrush Red = new SolidBrush(Color.RED);
    public static final SolidBrush Green = new SolidBrush(Color.GREEN);
    public static final SolidBrush White = new SolidBrush(Color.WHITE);
    public static final SolidBrush Yellow = new SolidBrush(Color.YELLOW);
    public static final SolidBrush Orange = new SolidBrush(Color.ORANGE);
    public static final SolidBrush Silver = new SolidBrush(new Color(192, 192, 192));
    public static final SolidBrush Transparent = new SolidBrush(new Color(0, 0, 0, 0));

    private Brushes() {
    }

    public static SolidBrush getBlack() {
        return Black;
    }

    public static SolidBrush getBlue() {
        return Blue;
    }

    public static SolidBrush getBrown() {
        return Brown;
    }

    public static SolidBrush getGray() {
        return Gray;
    }

    public static SolidBrush getLightGray() {
        return LightGray;
    }

    public static SolidBrush getRed() {
        return Red;
    }

    public static SolidBrush getGreen() {
        return Green;
    }

    public static SolidBrush getWhite() {
        return White;
    }

    public static SolidBrush getYellow() {
        return Yellow;
    }

    public static SolidBrush getOrange() {
        return Orange;
    }

    public static SolidBrush getSilver() {
        return Silver;
    }

    public static SolidBrush getTransparent() {
        return Transparent;
    }
}