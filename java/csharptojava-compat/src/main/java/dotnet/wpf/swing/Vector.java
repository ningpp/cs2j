package dotnet.wpf.swing;

/**
 * System.Windows.Vector compatibility value type.
 */
public final class Vector {

    private double x;
    private double y;

    public Vector() {
        this(0, 0);
    }

    public Vector(double x, double y) {
        this.x = x;
        this.y = y;
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

    public double getLength() {
        return Math.sqrt(x * x + y * y);
    }

    public double getLengthSquared() {
        return x * x + y * y;
    }

    public void normalize() {
        double length = getLength();
        if (length > 0 && !Double.isInfinite(length)) {
            x /= length;
            y /= length;
        }
    }

    public static Point add(Point point, Vector vector) {
        return new Point(point.getX() + vector.x, point.getY() + vector.y);
    }

    public static Point subtract(Point point, Vector vector) {
        return new Point(point.getX() - vector.x, point.getY() - vector.y);
    }

    public static Vector subtract(Point point1, Point point2) {
        return new Vector(point1.getX() - point2.getX(), point1.getY() - point2.getY());
    }

    public static Vector add(Vector vector1, Vector vector2) {
        return new Vector(vector1.x + vector2.x, vector1.y + vector2.y);
    }

    public static Vector subtract(Vector vector1, Vector vector2) {
        return new Vector(vector1.x - vector2.x, vector1.y - vector2.y);
    }

    public static Vector multiply(Vector vector, double scalar) {
        return new Vector(vector.x * scalar, vector.y * scalar);
    }

    public static Vector divide(Vector vector, double scalar) {
        return new Vector(vector.x / scalar, vector.y / scalar);
    }

    public static double crossProduct(Vector vector1, Vector vector2) {
        return vector1.x * vector2.y - vector1.y * vector2.x;
    }

    public static double angleBetween(Vector vector1, Vector vector2) {
        double dot = vector1.x * vector2.x + vector1.y * vector2.y;
        double len1 = vector1.getLength();
        double len2 = vector2.getLength();
        if (len1 == 0 || len2 == 0) {
            return 0;
        }
        double cos = dot / (len1 * len2);
        if (cos > 1) cos = 1;
        if (cos < -1) cos = -1;
        return Math.toDegrees(Math.acos(cos));
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Vector)) return false;
        Vector that = (Vector) o;
        return Double.compare(that.x, x) == 0 && Double.compare(that.y, y) == 0;
    }

    @Override
    public int hashCode() {
        return java.util.Objects.hash(x, y);
    }

    @Override
    public String toString() {
        return NetFormat.formatPoint(x, y);
    }
}
