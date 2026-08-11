package dotnet.drawing.swing.drawing2d;

/**
 * System.Drawing.Drawing2D.CompositingQuality compatibility enum.
 */
public enum CompositingQuality {
    Default(0), HighSpeed(1), HighQuality(2), GammaCorrected(3), AssumeLinear(4), Invalid(-1);

    private final int value;

    CompositingQuality(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static CompositingQuality fromValue(int v) {
        for (CompositingQuality e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static CompositingQuality fromValueUnchecked(int v) {
        return fromValue(v);
    }
}