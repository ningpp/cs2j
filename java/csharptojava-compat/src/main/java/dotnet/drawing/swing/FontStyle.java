package dotnet.drawing.swing;

/**
 * System.Drawing.FontStyle compatibility enum.
 */
public enum FontStyle {
    Regular(0), Bold(1), Italic(2), Underline(4), Strikeout(8);

    private final int value;

    FontStyle(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static FontStyle fromValue(int v) {
        for (FontStyle e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static FontStyle fromValueUnchecked(int v) {
        return fromValue(v);
    }
}