package dotnet.wpf.swing;

/**
 * System.Windows.MessageBoxResult compatibility enum (C# values).
 */
public enum MessageBoxResult {
    None(0),
    OK(1),
    Cancel(2),
    Yes(6),
    No(7);

    private final int value;

    MessageBoxResult(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static MessageBoxResult fromValue(int value) {
        for (MessageBoxResult e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static MessageBoxResult fromValueUnchecked(int value) {
        for (MessageBoxResult e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
