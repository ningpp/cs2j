package dotnet.wpf.swing;

/**
 * System.Windows.MessageBoxImage compatibility enum (C# values).
 */
public enum MessageBoxImage {
    None(0),
    Error(16),
    Hand(16),
    Stop(16),
    Question(32),
    Exclamation(48),
    Warning(48),
    Asterisk(64),
    Information(64);

    private final int value;

    MessageBoxImage(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static MessageBoxImage fromValue(int value) {
        for (MessageBoxImage e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static MessageBoxImage fromValueUnchecked(int value) {
        for (MessageBoxImage e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
