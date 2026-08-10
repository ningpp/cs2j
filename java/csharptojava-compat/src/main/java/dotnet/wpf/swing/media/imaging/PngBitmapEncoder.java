package dotnet.wpf.swing.media.imaging;

/**
 * System.Windows.Media.Imaging.PngBitmapEncoder compatibility class.
 */
public final class PngBitmapEncoder extends BitmapEncoder {

    @Override
    protected String getImageIoFormat() {
        return "png";
    }
}
