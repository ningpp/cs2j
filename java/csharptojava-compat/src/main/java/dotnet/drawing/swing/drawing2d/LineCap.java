package dotnet.drawing.swing.drawing2d;

/**
 * System.Drawing.Drawing2D.LineCap compatibility enum.
 */
public enum LineCap {
    Flat(0), Square(1), Round(2), Triangle(3), NoAnchor(16), SquareAnchor(17), RoundAnchor(18), DiamondAnchor(19),
    ArrowAnchor(20), Custom(255);

    private final int value;

    LineCap(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static LineCap fromValue(int v) {
        for (LineCap e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static LineCap fromValueUnchecked(int v) {
        return fromValue(v);
    }
}