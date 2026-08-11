package dotnet.drawing.swing.text;

/**
 * System.Drawing.Text.TextRenderingHint compatibility enum.
 */
public enum TextRenderingHint {
    SystemDefault(0), SingleBitPerPixelGridFit(1), SingleBitPerPixel(2), AntiAliasGridFit(3), AntiAlias(4),
    ClearTypeGridFit(5);

    private final int value;

    TextRenderingHint(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static TextRenderingHint fromValue(int v) {
        for (TextRenderingHint e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static TextRenderingHint fromValueUnchecked(int v) {
        return fromValue(v);
    }
}