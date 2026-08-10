package dotnet.wpf.swing;

/**
 * System.Windows.SystemParameters compatibility class (work area only, from AWT screen bounds).
 */
public final class SystemParameters {

    private SystemParameters() {
    }

    public static Rect getWorkArea() {
        java.awt.Rectangle bounds = java.awt.GraphicsEnvironment.getLocalGraphicsEnvironment()
                .getMaximumWindowBounds();
        return new Rect(0, 0, bounds.getWidth(), bounds.getHeight());
    }

    public static double getPrimaryScreenWidth() {
        return getWorkArea().getWidth();
    }

    public static double getPrimaryScreenHeight() {
        return getWorkArea().getHeight();
    }
}
