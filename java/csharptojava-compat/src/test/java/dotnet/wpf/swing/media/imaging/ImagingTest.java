package dotnet.wpf.swing.media.imaging;

import dotnet.wpf.swing.media.Brushes;
import dotnet.wpf.swing.media.RectangleGeometry;
import org.junit.jupiter.api.Test;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.nio.file.Files;

import static org.junit.jupiter.api.Assertions.*;

class ImagingTest {

    @Test
    void renderTargetBitmap_clearAndRender() {
        RenderTargetBitmap bmp = new RenderTargetBitmap(10, 10, 96, 96,
                dotnet.wpf.swing.media.PixelFormats.Pbgra32);
        assertNotNull(bmp.getImage());
        bmp.clear();
        assertEquals(10, bmp.getPixelWidth());
        assertEquals(10, bmp.getPixelHeight());
        bmp.render(dotnet.wpf.swing.media.Visual.class == null ? null : new VisualStub());
    }

    @Test
    void bitmapFrame_createFromSource() {
        RenderTargetBitmap bmp = new RenderTargetBitmap(8, 8, 96, 96,
                dotnet.wpf.swing.media.PixelFormats.Pbgra32);
        BitmapFrame frame = BitmapFrame.create(bmp);
        assertNotNull(frame);
        assertEquals(8, frame.getPixelWidth());
    }

    @Test
    void pngEncoder_savesPng() throws Exception {
        RenderTargetBitmap bmp = new RenderTargetBitmap(4, 4, 96, 96,
                dotnet.wpf.swing.media.PixelFormats.Pbgra32);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.getFrames().add(BitmapFrame.create(bmp));
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        encoder.save(out);
        byte[] data = out.toByteArray();
        // PNG magic number
        assertEquals((byte) 0x89, data[0]);
        assertEquals('P', data[1]);
        assertEquals('N', data[2]);
        assertEquals('G', data[3]);
    }

    @Test
    void bmpEncoder_savesBmp() throws Exception {
        RenderTargetBitmap bmp = new RenderTargetBitmap(4, 4, 96, 96,
                dotnet.wpf.swing.media.PixelFormats.Pbgra32);
        BmpBitmapEncoder encoder = new BmpBitmapEncoder();
        encoder.getFrames().add(BitmapFrame.create(bmp));
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        encoder.save(out);
        byte[] data = out.toByteArray();
        assertEquals('B', data[0]);
        assertEquals('M', data[1]);
    }

    @Test
    void bitmapImage_beginEndInit() {
        BitmapImage img = new BitmapImage();
        img.beginInit();
        img.setCreateOptions(BitmapCreateOptions.PreservePixelFormat);
        img.setCacheOption(BitmapCacheOption.None);
        img.endInit();
        assertEquals(BitmapCreateOptions.PreservePixelFormat, img.getCreateOptions());
        assertEquals(BitmapCacheOption.None, img.getCacheOption());
        img.freeze();
        assertTrue(img.isFrozen());
    }

    @Test
    void bitmapImage_loadsFromTempFile() throws Exception {
        java.nio.file.Path tmp = Files.createTempFile("wpf-test", ".png");
        try {
            java.awt.image.BufferedImage img = new java.awt.image.BufferedImage(16, 16,
                    java.awt.image.BufferedImage.TYPE_INT_ARGB);
            javax.imageio.ImageIO.write(img, "png", tmp.toFile());

            BitmapImage bi = new BitmapImage();
            bi.beginInit();
            bi.setUriSource(tmp.toUri());
            bi.endInit();
            assertEquals(16, bi.getPixelWidth());
            assertEquals(16, bi.getPixelHeight());
        } finally {
            Files.deleteIfExists(tmp);
        }
    }

    private static final class VisualStub extends dotnet.wpf.swing.media.Visual {
    }
}
