package dotnet.wpf.swing;

import java.util.Objects;

/**
 * System.Windows.Size compatibility value type.
 */
public final class Size {

    public static final Size EMPTY = new Size(Double.NaN, Double.NaN);

    private double width;
    private double height;

    public Size() {
        this(0, 0);
    }

    public Size(double width, double height) {
        this.width = width;
        this.height = height;
    }

    public Size(Size that) {
        this.width = that.width;
        this.height = that.height;
    }

    public double getWidth() {
        return width;
    }

    public void setWidth(double value) {
        width = value;
    }

    public double getHeight() {
        return height;
    }

    public void setHeight(double value) {
        height = value;
    }

    public boolean isEmpty() {
        return Double.isNaN(width) && Double.isNaN(height);
    }

    public static boolean equals(Size size1, Size size2) {
        if (size1 == null || size2 == null) {
            return size1 == size2;
        }
        return size1.equals(size2);
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Size)) return false;
        Size that = (Size) o;
        return Double.compare(that.width, width) == 0 && Double.compare(that.height, height) == 0;
    }

    @Override
    public int hashCode() {
        return Objects.hash(width, height);
    }

    @Override
    public String toString() {
        if (isEmpty()) {
            return "Empty";
        }
        return NetFormat.formatPoint(width, height);
    }
}
