package io.github.ningpp.compat;

import java.awt.Color;

/** System.Drawing.Color helper surface used by generated code. */
public final class DrawingColor {
    private DrawingColor() {
    }

    public static Color fromArgb(int r, int g, int b) {
        return new Color(clamp(r), clamp(g), clamp(b));
    }

    public static Color fromArgb(int a, int r, int g, int b) {
        return new Color(clamp(r), clamp(g), clamp(b), clamp(a));
    }

    public static byte getA(Color color) {
        return color == null ? 0 : (byte) color.getAlpha();
    }

    public static byte getR(Color color) {
        return color == null ? 0 : (byte) color.getRed();
    }

    public static byte getG(Color color) {
        return color == null ? 0 : (byte) color.getGreen();
    }

    public static byte getB(Color color) {
        return color == null ? 0 : (byte) color.getBlue();
    }

    public static boolean isEmpty(Color color) {
        return color == null || (color.getAlpha() == 0 && color.getRed() == 0 && color.getGreen() == 0 && color.getBlue() == 0);
    }

    public static Color fromName(String name) {
        if (name == null || name.isBlank()) {
            return new Color(0, 0, 0, 0);
        }

        try {
            java.lang.reflect.Field field = Color.class.getField(name.toLowerCase(java.util.Locale.ROOT));
            Object value = field.get(null);
            if (value instanceof Color color) {
                return color;
            }
        } catch (ReflectiveOperationException ignored) {
        }

        return switch (name.toLowerCase(java.util.Locale.ROOT).replace(" ", "")) {
            case "black" -> Color.BLACK;
            case "blue" -> Color.BLUE;
            case "cyan", "aqua" -> Color.CYAN;
            case "darkgray", "darkgrey" -> Color.DARK_GRAY;
            case "gray", "grey" -> Color.GRAY;
            case "green" -> Color.GREEN;
            case "lightgray", "lightgrey" -> Color.LIGHT_GRAY;
            case "magenta", "fuchsia" -> Color.MAGENTA;
            case "orange" -> Color.ORANGE;
            case "pink" -> Color.PINK;
            case "red" -> Color.RED;
            case "white" -> Color.WHITE;
            case "yellow" -> Color.YELLOW;
            default -> new Color(0, 0, 0, 0);
        };
    }

    public static Color getBlack() {
        return Color.BLACK;
    }

    private static int clamp(int value) {
        return Math.max(0, Math.min(255, value));
    }
}
