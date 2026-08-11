package dotnet.wpf.swing;

/**
 * System.Windows.HorizontalAlignment compatibility enum.
 */
public enum HorizontalAlignment {
    Left(0),
    Center(1),
    Right(2),
    Stretch(3);

    private final int value;

    HorizontalAlignment(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static HorizontalAlignment fromValue(int value) {
        for (HorizontalAlignment e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static HorizontalAlignment fromValueUnchecked(int value) {
        for (HorizontalAlignment e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}

