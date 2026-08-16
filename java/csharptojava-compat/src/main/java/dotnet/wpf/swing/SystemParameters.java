package dotnet.wpf.swing;

/**
 * System.Windows.SystemParameters compatibility class (work area only, from AWT screen bounds).
 */
public final class SystemParameters {

    private SystemParameters() {
    }

    public static Rect getWorkArea() {
        if (java.awt.GraphicsEnvironment.isHeadless()) {
            return new Rect(0, 0, 800, 600);
        }
        try {
            java.awt.Rectangle bounds = java.awt.GraphicsEnvironment.getLocalGraphicsEnvironment()
                    .getMaximumWindowBounds();
            return new Rect(0, 0, bounds.getWidth(), bounds.getHeight());
        } catch (java.awt.HeadlessException e) {
            return new Rect(0, 0, 800, 600);
        }
    }

    public static double getPrimaryScreenWidth() {
        return getWorkArea().getWidth();
    }

    public static double getPrimaryScreenHeight() {
        return getWorkArea().getHeight();
    }
}
