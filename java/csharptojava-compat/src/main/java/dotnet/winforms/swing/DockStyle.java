package dotnet.winforms.swing;

/**
 * System.Windows.Forms.DockStyle compatibility enum.
 */
public enum DockStyle {
    None(0),
    Top(1),
    Bottom(2),
    Left(3),
    Right(4),
    Fill(5);

    private final int value;

    DockStyle(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static DockStyle fromValue(int v) {
        for (DockStyle e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static DockStyle fromValueUnchecked(int v) {
        return fromValue(v);
    }
}

