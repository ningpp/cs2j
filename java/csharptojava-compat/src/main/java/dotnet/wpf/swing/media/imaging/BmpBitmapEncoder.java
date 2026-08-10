package dotnet.wpf.swing.media.imaging;

import java.io.IOException;
import java.io.OutputStream;

/**
 * System.Windows.Media.Imaging.BmpBitmapEncoder compatibility class.
 * Implements a minimal BMP encoder (BITMAPINFOHEADER + bottom-up 32bpp BGRA) so BMP
 * writing works even on JDKs whose ImageIO lacks a BMP writer.
 */
public final class BmpBitmapEncoder extends BitmapEncoder {

    @Override
    protected String getImageIoFormat() {
        return "bmp";
    }

    @Override
    public void save(OutputStream stream) throws IOException {
        if (getFrames().isEmpty()) {
            throw new IOException("No frames to save.");
        }
        java.awt.image.BufferedImage image = getFrames().get(0).getImage();
        if (image == null) {
            throw new IOException("Frame has no image data.");
        }
        writeBmp(image, stream);
    }

    static void writeBmp(java.awt.image.BufferedImage image, OutputStream stream) throws IOException {
        int width = image.getWidth();
        int height = Math.max(0, image.getHeight());
        int rowBytes = width * 4; // 32bpp BGRA
        int padding = (4 - (rowBytes % 4)) % 4;
        int pixelDataSize = (rowBytes + padding) * height;
        int fileSize = 14 + 40 + pixelDataSize;

        java.io.ByteArrayOutputStream raw = new java.io.ByteArrayOutputStream(pixelDataSize);
        byte[] row = new byte[rowBytes + padding];
        for (int y = height - 1; y >= 0; y--) {
            int idx = 0;
            for (int x = 0; x < width; x++) {
                int argb = image.getRGB(x, y);
                row[idx++] = (byte) (argb & 0xFF);
                row[idx++] = (byte) ((argb >> 8) & 0xFF);
                row[idx++] = (byte) ((argb >> 16) & 0xFF);
                row[idx++] = (byte) ((argb >> 24) & 0xFF);
            }
            for (int i = 0; i < padding; i++) {
                row[rowBytes + i] = 0;
            }
            raw.write(row, 0, rowBytes + padding);
        }

        // Bitmap file header (14 bytes)
        stream.write('B');
        stream.write('M');
        writeIntLE(stream, fileSize);
        writeShortLE(stream, 0); // reserved1
        writeShortLE(stream, 0); // reserved2
        writeIntLE(stream, 14 + 40); // pixel data offset

        // BITMAPINFOHEADER (40 bytes)
        writeIntLE(stream, 40);
        writeIntLE(stream, width);
        writeIntLE(stream, height);
        writeShortLE(stream, 1); // planes
        writeShortLE(stream, 32); // bpp
        writeIntLE(stream, 0); // no compression (BI_RGB)
        writeIntLE(stream, pixelDataSize);
        writeIntLE(stream, 0); // h resolution
        writeIntLE(stream, 0); // v resolution
        writeIntLE(stream, 0); // colors used
        writeIntLE(stream, 0); // important colors

        raw.writeTo(stream);
    }

    private static void writeShortLE(OutputStream s, int v) throws IOException {
        s.write(v & 0xFF);
        s.write((v >> 8) & 0xFF);
    }

    private static void writeIntLE(OutputStream s, long v) throws IOException {
        s.write((int) (v & 0xFF));
        s.write((int) ((v >> 8) & 0xFF));
        s.write((int) ((v >> 16) & 0xFF));
        s.write((int) ((v >> 24) & 0xFF));
    }
}
