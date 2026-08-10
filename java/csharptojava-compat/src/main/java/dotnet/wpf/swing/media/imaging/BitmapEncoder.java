package dotnet.wpf.swing.media.imaging;

import java.io.File;
import java.io.IOException;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.List;

import javax.imageio.ImageIO;

/**
 * System.Windows.Media.Imaging.BitmapEncoder compatibility class.
 * Swing-backed (ImageIO) PNG/BMP encoding.
 */
public abstract class BitmapEncoder {

    private final List<BitmapFrame> frames = new ArrayList<>();

    public List<BitmapFrame> getFrames() {
        return frames;
    }

    public void addFrame(BitmapFrame frame) {
        if (frame != null) {
            frames.add(frame);
        }
    }

    public void save(OutputStream stream) throws IOException {
        if (frames.isEmpty()) {
            throw new IOException("No frames to save.");
        }
        java.awt.image.BufferedImage image = frames.get(0).getImage();
        if (image == null) {
            throw new IOException("Frame has no image data.");
        }
        String format = getImageIoFormat();
        boolean ok = ImageIO.write(image, format, stream);
        if (!ok) {
            throw new IOException("No ImageIO writer found for format: " + format);
        }
    }

    public void save(String fileName) throws IOException {
        save(new File(fileName));
    }

    public void save(File file) throws IOException {
        if (frames.isEmpty()) {
            throw new IOException("No frames to save.");
        }
        java.awt.image.BufferedImage image = frames.get(0).getImage();
        String format = getImageIoFormat();
        boolean ok = ImageIO.write(image, format, file);
        if (!ok) {
            throw new IOException("No ImageIO writer found for format: " + format);
        }
    }

    protected abstract String getImageIoFormat();
}
