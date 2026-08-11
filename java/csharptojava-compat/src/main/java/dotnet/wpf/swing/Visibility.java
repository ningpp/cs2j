package dotnet.wpf.swing;

/**
 * System.Windows.Visibility compatibility enum.
 */
public enum Visibility {
    Visible(0),
    Hidden(1),
    Collapsed(2);

    private final int value;

    Visibility(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static Visibility fromValue(int value) {
        for (Visibility e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static Visibility fromValueUnchecked(int value) {
        for (Visibility e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}

