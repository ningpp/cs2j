package dotnet.winforms.swing;

/**
 * System.Windows.Forms.Padding compatibility value type.
 */
public final class Padding {

    public static final Padding Empty = new Padding(0, 0, 0, 0);

    private final int left;
    private final int top;
    private final int right;
    private final int bottom;

    public Padding(int all) {
        this(all, all, all, all);
    }

    public Padding(int left, int top, int right, int bottom) {
        this.left = left;
        this.top = top;
        this.right = right;
        this.bottom = bottom;
    }

    public int getLeft() {
        return left;
    }

    public int getTop() {
        return top;
    }

    public int getRight() {
        return right;
    }

    public int getBottom() {
        return bottom;
    }

    public int getHorizontal() {
        return left + right;
    }

    public int getVertical() {
        return top + bottom;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Padding)) return false;
        Padding that = (Padding) o;
        return left == that.left && top == that.top && right == that.right && bottom == that.bottom;
    }

    @Override
    public int hashCode() {
        return ((left * 397) ^ top) * 397 ^ right * 397 ^ bottom;
    }

    @Override
    public String toString() {
        return "{Left=" + left + ",Top=" + top + ",Right=" + right + ",Bottom=" + bottom + "}";
    }
}
