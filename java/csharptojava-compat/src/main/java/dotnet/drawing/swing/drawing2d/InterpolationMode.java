package dotnet.drawing.swing.drawing2d;

/**
 * System.Drawing.Drawing2D.InterpolationMode compatibility enum.
 */
public enum InterpolationMode {
    Default(0), Low(1), High(2), Bilinear(3), Bicubic(4), NearestNeighbor(5), HighQualityBilinear(6),
    HighQualityBicubic(7), Invalid(-1);

    private final int value;

    InterpolationMode(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static InterpolationMode fromValue(int v) {
        for (InterpolationMode e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static InterpolationMode fromValueUnchecked(int v) {
        return fromValue(v);
    }
}