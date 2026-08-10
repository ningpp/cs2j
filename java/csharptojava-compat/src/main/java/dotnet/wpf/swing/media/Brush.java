package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.Brush compatibility base class (Swing-backed).
 */
public abstract class Brush {

    private double opacity = 1.0;
    private Transform transform;
    private Transform relativeTransform;

    public double getOpacity() {
        return opacity;
    }

    public void setOpacity(double value) {
        opacity = value;
    }

    public Transform getTransform() {
        return transform;
    }

    public void setTransform(Transform value) {
        transform = value;
    }

    public Transform getRelativeTransform() {
        return relativeTransform;
    }

    public void setRelativeTransform(Transform value) {
        relativeTransform = value;
    }

    /**
     * Converts this brush to an AWT color (for Swing rendering).
     * Non-solid brushes fall back to transparent.
     */
    public java.awt.Color toAwtColor() {
        return new java.awt.Color(0, 0, 0, 0);
    }

    public Brush clone() {
        try {
            return (Brush) super.clone();
        } catch (CloneNotSupportedException e) {
            throw new RuntimeException(e);
        }
    }
}
