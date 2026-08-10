package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.GeometryHitTestParameters compatibility class.
 */
public final class GeometryHitTestParameters extends HitTestParameters {

    private final Geometry hitGeometry;

    public GeometryHitTestParameters(Geometry geometry) {
        this.hitGeometry = geometry;
    }

    public Geometry getHitGeometry() {
        return hitGeometry;
    }
}
