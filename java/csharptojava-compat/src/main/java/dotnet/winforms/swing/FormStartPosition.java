package dotnet.winforms.swing;

/**
 * System.Windows.Forms.FormStartPosition compatibility enum.
 */
public enum FormStartPosition {
    Manual(0),
    CenterScreen(1),
    WindowsDefaultLocation(2),
    WindowsDefaultBounds(3),
    CenterParent(4);

    private final int value;

    FormStartPosition(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static FormStartPosition fromValue(int v) {
        for (FormStartPosition e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static FormStartPosition fromValueUnchecked(int v) {
        return fromValue(v);
    }
}

