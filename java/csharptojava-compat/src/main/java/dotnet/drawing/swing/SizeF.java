package dotnet.drawing.swing;

/**
 * System.Drawing.SizeF compatibility class.
 */
public class SizeF {

    private float width;
    private float height;

    public SizeF() {
        this(0f, 0f);
    }

    public SizeF(float width, float height) {
        this.width = width;
        this.height = height;
    }

    public float getWidth() {
        return width;
    }

    public void setWidth(float width) {
        this.width = width;
    }

    public float getHeight() {
        return height;
    }

    public void setHeight(float height) {
        this.height = height;
    }
}