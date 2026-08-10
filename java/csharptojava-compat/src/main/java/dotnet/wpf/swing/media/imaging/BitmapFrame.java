package dotnet.wpf.swing.media.imaging;

import dotnet.wpf.swing.media.BitmapSource;

/**
 * System.Windows.Media.Imaging.BitmapFrame compatibility class.
 * A frame is a static bitmap source; supported image types are applied on save.
 */
public class BitmapFrame extends BitmapSource {

    protected BitmapFrame(java.awt.image.BufferedImage image, double dpiX, double dpiY) {
        super(image, dpiX, dpiY);
    }

    public static BitmapFrame create(BitmapSource source) {
        if (source instanceof BitmapFrame) {
            return (BitmapFrame) source;
        }
        return new BitmapFrame(source.getImage(), dpiFromSource(source, true), dpiFromSource(source, false));
    }

    private static double dpiFromSource(BitmapSource source, boolean x) {
        double w = source.getWidth();
        int pw = source.getPixelWidth();
        if (w > 0 && pw > 0) {
            return pw * 96.0 / w;
        }
        return 96;
    }

    public BitmapDecoder getDecoder() {
        return null;
    }
}
