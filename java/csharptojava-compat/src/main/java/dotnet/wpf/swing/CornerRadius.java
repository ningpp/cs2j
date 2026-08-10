package dotnet.wpf.swing;

import java.util.Objects;

/**
 * System.Windows.CornerRadius compatibility value type.
 */
public final class CornerRadius {

    private double topLeft;
    private double topRight;
    private double bottomRight;
    private double bottomLeft;

    public CornerRadius(double uniformRadius) {
        topLeft = topRight = bottomRight = bottomLeft = uniformRadius;
    }

    public CornerRadius(double topLeft, double topRight, double bottomRight, double bottomLeft) {
        this.topLeft = topLeft;
        this.topRight = topRight;
        this.bottomRight = bottomRight;
        this.bottomLeft = bottomLeft;
    }

    public double getTopLeft() { return topLeft; }
    public void setTopLeft(double value) { topLeft = value; }

    public double getTopRight() { return topRight; }
    public void setTopRight(double value) { topRight = value; }

    public double getBottomRight() { return bottomRight; }
    public void setBottomRight(double value) { bottomRight = value; }

    public double getBottomLeft() { return bottomLeft; }
    public void setBottomLeft(double value) { bottomLeft = value; }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof CornerRadius)) return false;
        CornerRadius that = (CornerRadius) o;
        return Double.compare(that.topLeft, topLeft) == 0
                && Double.compare(that.topRight, topRight) == 0
                && Double.compare(that.bottomRight, bottomRight) == 0
                && Double.compare(that.bottomLeft, bottomLeft) == 0;
    }

    @Override
    public int hashCode() {
        return Objects.hash(topLeft, topRight, bottomRight, bottomLeft);
    }

    @Override
    public String toString() {
        // .NET order: TopLeft, TopRight, BottomRight, BottomLeft.
        return NetFormat.format(topLeft) + "," + NetFormat.format(topRight) + ","
                + NetFormat.format(bottomRight) + "," + NetFormat.format(bottomLeft);
    }
}
