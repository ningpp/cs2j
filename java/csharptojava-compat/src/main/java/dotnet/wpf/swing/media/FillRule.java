package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.FillRule compatibility enum.
 */
public enum FillRule {
    EvenOdd(0),
    Nonzero(1);

    private final int value;

    FillRule(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static FillRule fromValue(int value) {
        for (FillRule e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static FillRule fromValueUnchecked(int value) {
        for (FillRule e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
