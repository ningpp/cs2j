package dotnet.drawing.swing;

/**
 * System.Drawing.Rectangle compatibility class.
 */
public class Rectangle {

    private int x;
    private int y;
    private int width;
    private int height;

    public Rectangle() {
        this(0, 0, 0, 0);
    }

    public Rectangle(int x, int y, int width, int height) {
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }

    public static Rectangle fromLTRB(int left, int top, int right, int bottom) {
        return new Rectangle(left, top, right - left, bottom - top);
    }

    public int getX() {
        return x;
    }

    public void setX(int x) {
        this.x = x;
    }

    public int getY() {
        return y;
    }

    public void setY(int y) {
        this.y = y;
    }

    public int getWidth() {
        return width;
    }

    public void setWidth(int width) {
        this.width = width;
    }

    public int getHeight() {
        return height;
    }

    public void setHeight(int height) {
        this.height = height;
    }

    public int getLeft() {
        return x;
    }

    public int getTop() {
        return y;
    }

    public int getRight() {
        return x + width;
    }

    public int getBottom() {
        return y + height;
    }

    public Size getSize() {
        return new Size(width, height);
    }

    public boolean contains(Point point) {
        return point != null && contains(point.getX(), point.getY());
    }

    public boolean contains(int px, int py) {
        return px >= x && px < x + width && py >= y && py < y + height;
    }
}