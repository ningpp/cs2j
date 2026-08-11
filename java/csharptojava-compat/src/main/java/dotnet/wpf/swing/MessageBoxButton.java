package dotnet.wpf.swing;

/**
 * System.Windows.MessageBoxButton compatibility enum (C# values).
 */
public enum MessageBoxButton {
    OK(0),
    OKCancel(1),
    YesNoCancel(3),
    YesNo(4);

    private final int value;

    MessageBoxButton(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static MessageBoxButton fromValue(int value) {
        for (MessageBoxButton e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static MessageBoxButton fromValueUnchecked(int value) {
        for (MessageBoxButton e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
