package dotnet.winforms.swing;

import java.awt.Color;

/**
 * System.Windows.Forms.ImageList compatibility class.
 */
public class ImageList {

    private ImageListStreamer imageStream;
    private final ImageCollection images = new ImageCollection();
    private Color transparentColor = new Color(0, 0, 0, 0);

    public ImageList() {
    }

    public ImageList(IContainer container) {
    }

    public ImageListStreamer getImageStream() {
        return imageStream;
    }

    public void setImageStream(ImageListStreamer value) {
        imageStream = value;
    }

    public ImageCollection getImages() {
        return images;
    }

    public Color getTransparentColor() {
        return transparentColor;
    }

    public void setTransparentColor(Color value) {
        transparentColor = value;
    }

    /**
     * System.Windows.Forms.ImageList.ImageCollection compatibility class.
     */
    public static class ImageCollection {

        public void setKeyName(int index, String name) {
            // no-op in the Swing-backed compat layer
        }
    }
}
