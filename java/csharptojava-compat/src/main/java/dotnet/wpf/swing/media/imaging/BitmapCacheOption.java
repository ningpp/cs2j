package dotnet.wpf.swing.media.imaging;

/**
 * System.Windows.Media.Imaging.BitmapCacheOption compatibility enum.
 */
public enum BitmapCacheOption {
    Default(0),
    OnDemand(1),
    OnLoad(2),
    None(3);

    private final int value;

    BitmapCacheOption(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static BitmapCacheOption fromValue(int value) {
        for (BitmapCacheOption e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static BitmapCacheOption fromValueUnchecked(int value) {
        for (BitmapCacheOption e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
