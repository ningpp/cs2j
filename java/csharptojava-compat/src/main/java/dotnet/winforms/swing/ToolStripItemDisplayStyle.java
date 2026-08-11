package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ToolStripItemDisplayStyle compatibility enum.
 */
public enum ToolStripItemDisplayStyle {
    None(0),
    Text(1),
    Image(2),
    ImageAndText(3);

    private final int value;

    ToolStripItemDisplayStyle(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static ToolStripItemDisplayStyle fromValue(int v) {
        for (ToolStripItemDisplayStyle e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static ToolStripItemDisplayStyle fromValueUnchecked(int v) {
        return fromValue(v);
    }
}

