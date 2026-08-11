package dotnet.winforms.swing;

import dotnet.drawing.swing.Rectangle;

/**
 * System.Windows.Forms.Screen compatibility class.
 */
public final class Screen {

    private static final Screen primary = new Screen();

    private Screen() {
    }

    public static final Screen PrimaryScreen = primary;

    public static Screen getPrimaryScreen() {
        return primary;
    }

    public Rectangle getWorkingArea() {
        java.awt.Rectangle bounds = java.awt.GraphicsEnvironment.getLocalGraphicsEnvironment()
                .getMaximumWindowBounds();
        return new Rectangle(bounds.x, bounds.y, bounds.width, bounds.height);
    }

    public Rectangle getBounds() {
        return getWorkingArea();
    }
}
