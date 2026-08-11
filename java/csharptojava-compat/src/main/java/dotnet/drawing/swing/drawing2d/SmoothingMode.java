package dotnet.drawing.swing.drawing2d;

/**
 * System.Drawing.Drawing2D.SmoothingMode compatibility enum.
 */
public enum SmoothingMode {
    Default(0), HighSpeed(1), AntiAlias(2), None(3), HighQuality(4), Invalid(-1);

    private final int value;

    SmoothingMode(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static SmoothingMode fromValue(int v) {
        for (SmoothingMode e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static SmoothingMode fromValueUnchecked(int v) {
        return fromValue(v);
    }
}