package dotnet.wpf.swing.media.imaging;

/**
 * System.Windows.Media.Imaging.BitmapCreateOptions compatibility flags (mapped to int by the converter).
 */
public final class BitmapCreateOptions {

    private BitmapCreateOptions() {
    }

    public static final int None = 0;
    public static final int PreservePixelFormat = 1;
    public static final int DelayCreation = 2;
    public static final int IgnoreColorProfile = 4;
    public static final int IgnoreImageCache = 8;
}
