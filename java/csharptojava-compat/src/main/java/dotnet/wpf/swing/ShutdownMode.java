package dotnet.wpf.swing;

/**
 * System.Windows.ShutdownMode compatibility enum.
 */
public enum ShutdownMode {
    OnLastWindowClose(0),
    OnMainWindowClose(1),
    OnExplicitShutdown(2);

    private final int value;

    ShutdownMode(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static ShutdownMode fromValue(int value) {
        for (ShutdownMode e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static ShutdownMode fromValueUnchecked(int value) {
        return fromValue(value);
    }
}