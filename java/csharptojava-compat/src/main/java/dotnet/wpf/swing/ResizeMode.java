package dotnet.wpf.swing;

/**
 * System.Windows.ResizeMode compatibility enum.
 */
public enum ResizeMode {
    NoResize(0),
    CanMinimize(1),
    CanResize(2),
    CanResizeWithGrip(3);

    private final int value;

    ResizeMode(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static ResizeMode fromValue(int value) {
        for (ResizeMode e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static ResizeMode fromValueUnchecked(int value) {
        return fromValue(value);
    }
}