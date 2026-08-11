package dotnet.drawing.swing.drawing2d;

/**
 * System.Drawing.Drawing2D.DashStyle compatibility enum.
 */
public enum DashStyle {
    Solid(0), Dash(1), Dot(2), DashDot(3), DashDotDot(4), Custom(5);

    private final int value;

    DashStyle(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static DashStyle fromValue(int v) {
        for (DashStyle e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static DashStyle fromValueUnchecked(int v) {
        return fromValue(v);
    }
}