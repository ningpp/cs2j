package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.SweepDirection compatibility enum.
 */
public enum SweepDirection {
    Counterclockwise(0),
    Clockwise(1);

    private final int value;

    SweepDirection(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static SweepDirection fromValue(int value) {
        for (SweepDirection e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static SweepDirection fromValueUnchecked(int value) {
        for (SweepDirection e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
