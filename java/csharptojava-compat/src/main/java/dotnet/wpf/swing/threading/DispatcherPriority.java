package dotnet.wpf.swing.threading;

/**
 * System.Windows.Threading.DispatcherPriority compatibility enum.
 * Values mirror the .NET enum: {@code Invalid(-1)} through {@code Send(10)}.
 * The Swing-backed dispatcher treats all priorities above {@code Inactive}
 * as equivalent, but they are preserved for API compatibility.
 */
public enum DispatcherPriority {
    Invalid(-1),
    Inactive(0),
    SystemIdle(1),
    ApplicationIdle(2),
    ContextIdle(3),
    Background(4),
    Input(5),
    Loaded(6),
    Render(7),
    DataBind(8),
    Normal(9),
    Send(10);

    private final int value;

    DispatcherPriority(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static DispatcherPriority fromValue(int value) {
        for (DispatcherPriority e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return Normal;
    }

    public static DispatcherPriority fromValueUnchecked(int value) {
        return fromValue(value);
    }
}