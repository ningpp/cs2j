package dotnet.wpf.swing;

/**
 * System.Windows.WindowStyle compatibility enum.
 */
public enum WindowStyle {
    None(0),
    SingleBorderWindow(1),
    ThreeDBorderWindow(2),
    ToolWindow(3);

    private final int value;

    WindowStyle(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static WindowStyle fromValue(int value) {
        for (WindowStyle e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static WindowStyle fromValueUnchecked(int value) {
        return fromValue(value);
    }
}