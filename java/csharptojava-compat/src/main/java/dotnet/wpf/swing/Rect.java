package dotnet.wpf.swing;

import java.util.Objects;

/**
 * System.Windows.Rect compatibility value type.
 */
public final class Rect {

    public static final Rect EMPTY = new Rect(Double.NaN, Double.NaN, Double.NaN, Double.NaN);

    private double x;
    private double y;
    private double width;
    private double height;

    public Rect() {
        this(0, 0, 0, 0);
    }

    public Rect(Size size) {
        this(0, 0, size.getWidth(), size.getHeight());
    }

    public Rect(Point location, Size size) {
        this(location.getX(), location.getY(), size.getWidth(), size.getHeight());
    }

    public Rect(Point point1, Point point2) {
        this.x = Math.min(point1.getX(), point2.getX());
        this.y = Math.min(point1.getY(), point2.getY());
        this.width = Math.max(point1.getX(), point2.getX()) - this.x;
        this.height = Math.max(point1.getY(), point2.getY()) - this.y;
    }

    public Rect(double x, double y, double width, double height) {
        // .NET 6 WPF throws ArgumentException for negative width/height (no normalization).
        if (width < 0 || height < 0) {
            throw new IllegalArgumentException("Width and height must be non-negative.");
        }
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }

    public Rect(double x, double y, double width, double height, boolean allowEmpty) {
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }

    public double getX() { return x; }
    public void setX(double value) { x = value; }

    public double getY() { return y; }
    public void setY(double value) { y = value; }

    public double getWidth() { return width; }
    public void setWidth(double value) { width = value; }

    public double getHeight() { return height; }
    public void setHeight(double value) { height = value; }

    public double getLeft() { return x; }
    public double getTop() { return y; }
    public double getRight() { return x + width; }
    public double getBottom() { return y + height; }

    public Point getTopLeft() { return new Point(x, y); }
    public Point getTopRight() { return new Point(x + width, y); }
    public Point getBottomLeft() { return new Point(x, y + height); }
    public Point getBottomRight() { return new Point(x + width, y + height); }

    public Point getLocation() { return new Point(x, y); }
    public void setLocation(Point value) { x = value.getX(); y = value.getY(); }

    public Size getSize() { return new Size(width, height); }
    public void setSize(Size value) { width = value.getWidth(); height = value.getHeight(); }

    public boolean isEmpty() {
        return Double.isNaN(x) && Double.isNaN(y) && Double.isNaN(width) && Double.isNaN(height);
    }

    public boolean contains(Point point) {
        return contains(point.getX(), point.getY());
    }

    public boolean contains(double px, double py) {
        if (isEmpty()) return false;
        if (width < 0 || height < 0) return false;
        return px >= x && px <= x + width && py >= y && py <= y + height;
    }

    public boolean contains(Rect rect) {
        if (isEmpty() || rect.isEmpty()) return false;
        return rect.x >= x && rect.right() <= right()
                && rect.y >= y && rect.bottom() <= bottom();
    }

    public boolean intersectsWith(Rect rect) {
        if (isEmpty() || rect.isEmpty()) return false;
        // .NET WPF: inclusive of edges (touch counts as intersect).
        return rect.left() <= right() && rect.right() >= left()
                && rect.top() <= bottom() && rect.bottom() >= top();
    }

    public void intersect(Rect rect) {
        if (isEmpty() || rect.isEmpty()) {
            setEmpty();
            return;
        }
        double nx = Math.max(left(), rect.left());
        double ny = Math.max(top(), rect.top());
        double nr = Math.min(right(), rect.right());
        double nb = Math.min(bottom(), rect.bottom());
        if (nx > nr || ny > nb) {
            setEmpty();
            return;
        }
        x = nx; y = ny; width = nr - nx; height = nb - ny;
    }

    public void union(Rect rect) {
        if (isEmpty()) {
            copyFrom(rect);
            return;
        }
        if (rect.isEmpty()) return;
        double nx = Math.min(left(), rect.left());
        double ny = Math.min(top(), rect.top());
        double nr = Math.max(right(), rect.right());
        double nb = Math.max(bottom(), rect.bottom());
        x = nx; y = ny; width = nr - nx; height = nb - ny;
    }

    public void union(Point point) {
        if (isEmpty()) {
            x = point.getX(); y = point.getY(); width = 0; height = 0;
            return;
        }
        double nx = Math.min(left(), point.getX());
        double ny = Math.min(top(), point.getY());
        double nr = Math.max(right(), point.getX());
        double nb = Math.max(bottom(), point.getY());
        x = nx; y = ny; width = nr - nx; height = nb - ny;
    }

    public static Rect union(Rect rect1, Rect rect2) {
        Rect r = new Rect(rect1);
        r.union(rect2);
        return r;
    }

    public static Rect union(Rect rect, Point point) {
        Rect r = new Rect(rect);
        r.union(point);
        return r;
    }

    public static Rect intersect(Rect rect1, Rect rect2) {
        Rect r = new Rect(rect1);
        r.intersect(rect2);
        return r;
    }

    public void offset(double offsetX, double offsetY) {
        x += offsetX;
        y += offsetY;
    }

    public void offset(Point offsetVector) {
        offset(offsetVector.getX(), offsetVector.getY());
    }

    public static Rect offset(Rect rect, double offsetX, double offsetY) {
        Rect r = new Rect(rect);
        r.offset(offsetX, offsetY);
        return r;
    }

    public void inflate(double widthDelta, double heightDelta) {
        x -= widthDelta;
        y -= heightDelta;
        width += 2 * widthDelta;
        height += 2 * heightDelta;
    }

    public void inflate(Size size) {
        inflate(size.getWidth(), size.getHeight());
    }

    public void scale(double scaleX, double scaleY) {
        x *= scaleX;
        y *= scaleY;
        width *= scaleX;
        height *= scaleY;
    }

    /**
     * .NET instance form: transforms this rect in place (returns void in .NET; here we return
     * this for convenience but still mutate in place).
     */
    public Rect transform(dotnet.wpf.swing.media.Matrix matrix) {
        Point tl = matrix.transform(new Point(left(), top()));
        Point tr = matrix.transform(new Point(right(), top()));
        Point bl = matrix.transform(new Point(left(), bottom()));
        Point br = matrix.transform(new Point(right(), bottom()));
        Rect result = new Rect(tl, tr);
        result.union(bl);
        result.union(br);
        copyFrom(result);
        return this;
    }

    public static Rect transform(Rect rect, dotnet.wpf.swing.media.Matrix matrix) {
        Rect result = new Rect(rect);
        result.transform(matrix);
        return result;
    }

    public static boolean equals(Rect rect1, Rect rect2) {
        if (rect1 == null || rect2 == null) return rect1 == rect2;
        return rect1.equals(rect2);
    }

    private double left() { return x; }
    private double right() { return x + width; }
    private double top() { return y; }
    private double bottom() { return y + height; }

    private void setEmpty() {
        x = y = width = height = Double.NaN;
    }

    private void copyFrom(Rect that) {
        x = that.x; y = that.y; width = that.width; height = that.height;
    }

    public Rect(Rect that) {
        copyFrom(that);
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Rect)) return false;
        Rect that = (Rect) o;
        return Double.compare(that.x, x) == 0
                && Double.compare(that.y, y) == 0
                && Double.compare(that.width, width) == 0
                && Double.compare(that.height, height) == 0;
    }

    @Override
    public int hashCode() {
        return Objects.hash(x, y, width, height);
    }

    @Override
    public String toString() {
        if (isEmpty()) {
            return "Empty";
        }
        return NetFormat.format(x) + "," + NetFormat.format(y) + ","
                + NetFormat.format(width) + "," + NetFormat.format(height);
    }
}
