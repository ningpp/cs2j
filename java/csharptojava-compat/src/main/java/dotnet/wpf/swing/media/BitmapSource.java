package dotnet.wpf.swing.media;

import java.awt.image.BufferedImage;

/**
 * System.Windows.Media.BitmapSource compatibility base class (Swing BufferedImage-backed).
 */
public abstract class BitmapSource extends ImageSource {

    protected java.awt.image.BufferedImage image;
    private int pixelWidth;
    private int pixelHeight;
    private final double dpiX;
    private final double dpiY;

    protected BitmapSource(java.awt.image.BufferedImage image, double dpiX, double dpiY) {
        this.image = image;
        this.pixelWidth = image.getWidth();
        this.pixelHeight = image.getHeight();
        this.dpiX = dpiX;
        this.dpiY = dpiY;
    }

    protected BitmapSource(int pixelWidth, int pixelHeight, double dpiX, double dpiY) {
        this(new java.awt.image.BufferedImage(Math.max(1, pixelWidth), Math.max(1, pixelHeight),
                java.awt.image.BufferedImage.TYPE_INT_ARGB), dpiX, dpiY);
    }

    protected void replaceInternal(java.awt.image.BufferedImage img) {
        this.image = img;
        this.pixelWidth = img.getWidth();
        this.pixelHeight = img.getHeight();
    }

    public BufferedImage getImage() {
        return image;
    }

    public int getPixelWidth() {
        return pixelWidth;
    }

    public int getPixelHeight() {
        return pixelHeight;
    }

    @Override
    public double getWidth() {
        return dpiX > 0 ? pixelWidth * 96.0 / dpiX : pixelWidth;
    }

    @Override
    public double getHeight() {
        return dpiY > 0 ? pixelHeight * 96.0 / dpiY : pixelHeight;
    }

    public void copyPixels(int[] pixels, int stride, int offset) {
        image.getRGB(0, 0, pixelWidth, pixelHeight, pixels, offset, stride);
    }
}
