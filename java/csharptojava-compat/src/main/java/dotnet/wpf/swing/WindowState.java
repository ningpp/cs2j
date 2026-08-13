package dotnet.wpf.swing;

/**
 * System.Windows.WindowState compatibility enum.
 */
public enum WindowState {
    Normal(0),
    Minimized(1),
    Maximized(2);

    private final int value;

    WindowState(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static WindowState fromValue(int value) {
        for (WindowState e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static WindowState fromValueUnchecked(int value) {
        return fromValue(value);
    }
}