package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.PixelFormats compatibility class (static pixel format instances).
 */
public final class PixelFormats {

    private PixelFormats() {
    }

    public static final PixelFormat Bgr101010 = new PixelFormat(10);
    public static final PixelFormat Bgr24 = PixelFormat.Bgr24;
    public static final PixelFormat Bgr32 = PixelFormat.Bgr32;
    public static final PixelFormat Bgr555 = new PixelFormat(14);
    public static final PixelFormat Bgr565 = new PixelFormat(15);
    public static final PixelFormat Bgra32 = PixelFormat.Bgra32;
    public static final PixelFormat BlackWhite = new PixelFormat(1);
    public static final PixelFormat Cmyk32 = new PixelFormat(2);
    public static final PixelFormat Default = PixelFormat.Default;
    public static final PixelFormat Gray16 = new PixelFormat(6);
    public static final PixelFormat Gray2 = new PixelFormat(4);
    public static final PixelFormat Gray4 = new PixelFormat(5);
    public static final PixelFormat Gray8 = PixelFormat.Gray8;
    public static final PixelFormat Indexed1 = new PixelFormat(3);
    public static final PixelFormat Indexed2 = new PixelFormat(4);
    public static final PixelFormat Indexed4 = new PixelFormat(5);
    public static final PixelFormat Indexed8 = PixelFormat.Gray8;
    public static final PixelFormat Pbgra32 = PixelFormat.Pbgra32;
    public static final PixelFormat prgba128Float = PixelFormat.Prgba128Float;
    public static final PixelFormat Prgba64 = new PixelFormat(16);
    public static final PixelFormat Rgb24 = PixelFormat.Rgb24;
    public static final PixelFormat Rgb48 = PixelFormat.Rgb48;
    public static final PixelFormat Rgba64 = PixelFormat.Rgba64;

    public static PixelFormat getBgr101010() { return Bgr101010; }
    public static PixelFormat getBgr24() { return Bgr24; }
    public static PixelFormat getBgr32() { return Bgr32; }
    public static PixelFormat getBgr555() { return Bgr555; }
    public static PixelFormat getBgr565() { return Bgr565; }
    public static PixelFormat getBgra32() { return Bgra32; }
    public static PixelFormat getBlackWhite() { return BlackWhite; }
    public static PixelFormat getCmyk32() { return Cmyk32; }
    public static PixelFormat getDefault() { return Default; }
    public static PixelFormat getGray16() { return Gray16; }
    public static PixelFormat getGray2() { return Gray2; }
    public static PixelFormat getGray4() { return Gray4; }
    public static PixelFormat getGray8() { return Gray8; }
    public static PixelFormat getIndexed1() { return Indexed1; }
    public static PixelFormat getIndexed2() { return Indexed2; }
    public static PixelFormat getIndexed4() { return Indexed4; }
    public static PixelFormat getIndexed8() { return Indexed8; }
    public static PixelFormat getPbgra32() { return Pbgra32; }
    public static PixelFormat getPrgba128Float() { return prgba128Float; }
    public static PixelFormat getPrgba64() { return Prgba64; }
    public static PixelFormat getRgb24() { return Rgb24; }
    public static PixelFormat getRgb48() { return Rgb48; }
    public static PixelFormat getRgba64() { return Rgba64; }
}
