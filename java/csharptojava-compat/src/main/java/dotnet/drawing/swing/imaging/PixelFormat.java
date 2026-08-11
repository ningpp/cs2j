package dotnet.drawing.swing.imaging;

/**
 * System.Drawing.Imaging.PixelFormat compatibility enum.
 */
public enum PixelFormat {
    DontCare(0), Format32bppArgb(2498570), Format32bppPArgb(2498570), Format24bppRgb(137224), Format1bppIndexed(196869);

    private final int value;

    PixelFormat(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static PixelFormat fromValue(int v) {
        for (PixelFormat e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static PixelFormat fromValueUnchecked(int v) {
        return fromValue(v);
    }
}