package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.ModifierKeys compatibility enum (flags).
 */
public enum ModifierKeys {
    None(0),
    Alt(1),
    Control(2),
    Shift(4),
    Windows(8);

    private final int value;

    ModifierKeys(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }
}
