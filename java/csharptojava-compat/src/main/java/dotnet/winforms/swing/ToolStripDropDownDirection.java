package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ToolStripDropDownDirection compatibility enum.
 */
public enum ToolStripDropDownDirection {
    AboveLeft(0),
    AboveRight(1),
    BelowLeft(2),
    BelowRight(3),
    Left(4),
    Right(5);

    private final int value;

    ToolStripDropDownDirection(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static ToolStripDropDownDirection fromValue(int v) {
        for (ToolStripDropDownDirection e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static ToolStripDropDownDirection fromValueUnchecked(int v) {
        return fromValue(v);
    }
}

