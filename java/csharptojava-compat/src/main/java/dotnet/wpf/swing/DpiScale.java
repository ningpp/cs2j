package dotnet.wpf.swing;

/**
 * System.Windows.DpiScale compatibility value.
 */
public final class DpiScale {

    private final double dpiScaleX;
    private final double dpiScaleY;

    public DpiScale() {
        this(1.0, 1.0);
    }

    public DpiScale(double dpiScaleX, double dpiScaleY) {
        this.dpiScaleX = dpiScaleX;
        this.dpiScaleY = dpiScaleY;
    }

    public double getDpiScaleX() {
        return dpiScaleX;
    }

    public double getDpiScaleY() {
        return dpiScaleY;
    }

    /**
     * Pixels per device-independent unit (usually 96 DPI → 1.0, 192 DPI → 2.0).
     */
    public double getPixelsPerDip() {
        return dpiScaleX;
    }
}
