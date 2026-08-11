package dotnet.drawing.swing;

/**
 * System.Drawing.PointF compatibility class.
 */
public class PointF {

    private float x;
    private float y;

    public PointF() {
        this(0f, 0f);
    }

    public PointF(float x, float y) {
        this.x = x;
        this.y = y;
    }

    public PointF(PointF other) {
        this(other != null ? other.getX() : 0f, other != null ? other.getY() : 0f);
    }

    public float getX() {
        return x;
    }

    public void setX(float x) {
        this.x = x;
    }

    public float getY() {
        return y;
    }

    public void setY(float y) {
        this.y = y;
    }
}