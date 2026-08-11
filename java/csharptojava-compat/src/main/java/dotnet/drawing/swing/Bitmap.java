package dotnet.drawing.swing;

import java.awt.image.BufferedImage;
import java.io.File;
import java.io.IOException;
import java.util.Locale;

import javax.imageio.ImageIO;

import dotnet.drawing.swing.imaging.PixelFormat;

/**
 * System.Drawing.Bitmap compatibility class.
 */
public class Bitmap extends Image {

    private final int width;
    private final int height;
    private final BufferedImage bufferedImage;

    public Bitmap(int width, int height) {
        this(width, height, PixelFormat.Format32bppArgb);
    }

    public Bitmap(int width, int height, PixelFormat pixelFormat) {
        this.width = width;
        this.height = height;
        this.bufferedImage = new BufferedImage(width, height, BufferedImage.TYPE_INT_ARGB);
    }

    @Override
    public int getWidth() {
        return width;
    }

    @Override
    public int getHeight() {
        return height;
    }

    @Override
    public void save(String filename) {
        if (filename == null || filename.isEmpty()) {
            return;
        }
        String name = filename.toLowerCase(Locale.ROOT).endsWith(".png") ? filename : filename + ".png";
        try {
            ImageIO.write(bufferedImage, "png", new File(name));
        } catch (IOException e) {
            throw new RuntimeException("Failed to save bitmap: " + name, e);
        }
    }

    public BufferedImage getBufferedImage() {
        return bufferedImage;
    }

    public Graphics createGraphics() {
        return new Graphics(bufferedImage.createGraphics());
    }
}