package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.ImageSource compatibility base class (bitmap/image source).
 */
public abstract class ImageSource {

    public abstract double getWidth();

    public abstract double getHeight();

    private final String cacheKey = java.util.UUID.randomUUID().toString();

    public String getCacheKey() {
        return cacheKey;
    }
}
