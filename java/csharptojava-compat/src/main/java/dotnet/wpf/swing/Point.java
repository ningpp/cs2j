package dotnet.wpf.swing;

import java.util.Objects;

/**
 * System.Windows.Point compatibility value type.
 */
public final class Point {

    private double x;
    private double y;

    public Point() {
        this(0, 0);
    }

    public Point(double x, double y) {
        this.x = x;
        this.y = y;
    }

    public Point(Size size) {
        this(size.getWidth(), size.getHeight());
    }

    public Point(Point that) {
        this.x = that.x;
        this.y = that.y;
    }

    public double getX() {
        return x;
    }

    public void setX(double value) {
        x = value;
    }

    public double getY() {
        return y;
    }

    public void setY(double value) {
        y = value;
    }

    public void offset(double offsetX, double offsetY) {
        x += offsetX;
        y += offsetY;
    }

    public void offset(Point point) {
        offset(point.x, point.y);
    }

    public static Point add(Point point, Point vector) {
        return new Point(point.x + vector.x, point.y + vector.y);
    }

    public static Point subtract(Point point, Point vector) {
        return new Point(point.x - vector.x, point.y - vector.y);
    }

    // .NET overloads using Vector operands
    public static Point add(Point point, Vector vector) {
        return Vector.add(point, vector);
    }

    public static Point subtract(Point point, Vector vector) {
        return Vector.subtract(point, vector);
    }

    /** .NET: Point - Point => Vector */
    public static Vector subtractToVector(Point point1, Point point2) {
        return Vector.subtract(point1, point2);
    }

    public static boolean equals(Point point1, Point point2) {
        if (point1 == null || point2 == null) {
            return point1 == point2;
        }
        return point1.equals(point2);
    }

    // C# == on a value type compares by value.
    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Point)) return false;
        Point that = (Point) o;
        return Double.compare(that.x, x) == 0 && Double.compare(that.y, y) == 0;
    }

    @Override
    public int hashCode() {
        return Objects.hash(x, y);
    }

    @Override
    public String toString() {
        return NetFormat.formatPoint(x, y);
    }
}
