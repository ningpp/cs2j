package dotnet.wpf.swing;

/**
 * System.Windows.WindowStartupLocation compatibility enum.
 */
public enum WindowStartupLocation {
    Manual(0),
    CenterScreen(1),
    CenterOwner(2);

    private final int value;

    WindowStartupLocation(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static WindowStartupLocation fromValue(int value) {
        for (WindowStartupLocation e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static WindowStartupLocation fromValueUnchecked(int value) {
        return fromValue(value);
    }
}