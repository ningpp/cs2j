package dotnet.drawing.swing.imaging;

import dotnet.drawing.swing.Image;
import dotnet.drawing.swing.Rectangle;

/**
 * System.Drawing.Imaging.Metafile compatibility class.
 */
public class Metafile extends Image {

    public Metafile(Object stream, long hdc, EmfType emfType) {
    }

    public Metafile(long hdc, Rectangle rect) {
    }

    @Override
    public int getWidth() {
        return 0;
    }

    @Override
    public int getHeight() {
        return 0;
    }

    @Override
    public void save(String filename) {
    }
}