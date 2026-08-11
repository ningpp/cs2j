package dotnet.wpf.swing;

/**
 * System.Windows.VerticalAlignment compatibility enum.
 */
public enum VerticalAlignment {
    Top(0),
    Center(1),
    Bottom(2),
    Stretch(3);

    private final int value;

    VerticalAlignment(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static VerticalAlignment fromValue(int value) {
        for (VerticalAlignment e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static VerticalAlignment fromValueUnchecked(int value) {
        for (VerticalAlignment e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}

