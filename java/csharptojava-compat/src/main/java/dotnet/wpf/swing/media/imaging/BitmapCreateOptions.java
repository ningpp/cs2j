package dotnet.wpf.swing.media.imaging;

/**
 * System.Windows.Media.Imaging.BitmapCreateOptions compatibility enum.
 */
public enum BitmapCreateOptions {
    None(0),
    PreservePixelFormat(1),
    DelayCreation(2),
    IgnoreColorProfile(4),
    IgnoreImageCache(8);

    private final int value;

    BitmapCreateOptions(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }
}
