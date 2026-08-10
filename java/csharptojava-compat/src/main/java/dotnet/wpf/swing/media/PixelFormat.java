package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.PixelFormat compatibility value.
 */
public final class PixelFormat {

    public static final PixelFormat Bgr24 = new PixelFormat(11);
    public static final PixelFormat Bgr32 = new PixelFormat(12);
    public static final PixelFormat Bgra32 = new PixelFormat(13);
    public static final PixelFormat Pbgra32 = new PixelFormat(17);
    public static final PixelFormat Rgb24 = new PixelFormat(19);
    public static final PixelFormat Rgb48 = new PixelFormat(20);
    public static final PixelFormat Rgba64 = new PixelFormat(21);
    public static final PixelFormat Gray8 = new PixelFormat(7);
    public static final PixelFormat Prgba128Float = new PixelFormat(26);
    public static final PixelFormat Default = new PixelFormat(0);

    private final int value;
    private final boolean isBgra;

    private static final int VALUE_BGRA32 = 13;
    private static final int VALUE_PBGRA32 = 17;
    private static final int VALUE_PRGBA128FLOAT = 26;

    PixelFormat(int value) {
        this.value = value;
        this.isBgra = value == VALUE_BGRA32 || value == VALUE_PBGRA32 || value == VALUE_PRGBA128FLOAT;
    }

    public boolean isMcg32() {
        return isBgra;
    }

    public boolean isAlphaFirst() {
        // Pbgra32 has unpremultiplied alpha first; Bgra32 is premultiplied
        return value == VALUE_PBGRA32 || value == VALUE_PRGBA128FLOAT;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof PixelFormat)) return false;
        return ((PixelFormat) o).value == value;
    }

    @Override
    public int hashCode() {
        return Integer.hashCode(value);
    }

    @Override
    public String toString() {
        return "PixelFormat(" + value + ")";
    }
}
