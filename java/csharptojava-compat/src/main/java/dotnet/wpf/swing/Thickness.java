package dotnet.wpf.swing;

/**
 * System.Windows.Thickness compatibility value type (uniform binding approximated as a double margin).
 */
public final class Thickness {

    private final double left;
    private final double top;
    private final double right;
    private final double bottom;

    public Thickness(double uniformLength) {
        this.left = this.top = this.right = this.bottom = uniformLength;
    }

    public Thickness(double left, double top, double right, double bottom) {
        this.left = left;
        this.top = top;
        this.right = right;
        this.bottom = bottom;
    }

    public double getLeft() { return left; }
    public double getTop() { return top; }
    public double getRight() { return right; }
    public double getBottom() { return bottom; }

    public boolean isEmpty() {
        return left == 0 && top == 0 && right == 0 && bottom == 0;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Thickness)) return false;
        Thickness t = (Thickness) o;
        return left == t.left && top == t.top && right == t.right && bottom == t.bottom;
    }

    @Override
    public int hashCode() {
        return java.util.Objects.hash(left, top, right, bottom);
    }

    @Override
    public String toString() {
        return left + "," + top + "," + right + "," + bottom;
    }
}
