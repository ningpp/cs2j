package dotnet.drawing.swing;

/**
 * System.Drawing.Image compatibility class.
 */
public abstract class Image {

    public abstract int getWidth();

    public abstract int getHeight();

    public void save(String filename) {
    }
}