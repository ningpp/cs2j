package dotnet.winforms.swing;

/**
 * System.Windows.Forms.TickStyle compatibility enum.
 */
public enum TickStyle {
    None(0),
    TopLeft(1),
    BottomRight(2),
    Both(3);

    private final int value;

    TickStyle(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static TickStyle fromValue(int v) {
        for (TickStyle e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static TickStyle fromValueUnchecked(int v) {
        return fromValue(v);
    }
}

