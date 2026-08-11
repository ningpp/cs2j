package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.PenLineCap compatibility enum.
 */
public enum PenLineCap {
    Flat(0),
    Square(1),
    Round(2),
    Triangle(3);

    private final int value;

    PenLineCap(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static PenLineCap fromValue(int value) {
        for (PenLineCap e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static PenLineCap fromValueUnchecked(int value) {
        for (PenLineCap e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
