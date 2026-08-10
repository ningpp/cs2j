package dotnet.wpf.swing.input;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Input.Mouse compatibility static class.
 * Swing backend tracks the last known cursor position.
 */
public final class Mouse {

    private Mouse() {
    }

    private static boolean captured;
    private static Object capturedElement;

    public static Point getPosition(Object relativeTo) {
        java.awt.Point p = java.awt.MouseInfo.getPointerInfo() == null
                ? new java.awt.Point()
                : java.awt.MouseInfo.getPointerInfo().getLocation();
        return new Point(p.getX(), p.getY());
    }

    public static Object getCaptured() {
        return capturedElement;
    }

    public static MouseButtonState getLeftButton() {
        return readButton(1);
    }

    public static MouseButtonState getRightButton() {
        return readButton(3);
    }

    public static MouseButtonState getMiddleButton() {
        return readButton(2);
    }

    public static boolean capture(Object element) {
        captured = true;
        capturedElement = element;
        return true;
    }

    public static void synchronize() {
    }

    public static void updateCursor() {
    }

    private static MouseButtonState readButton(int swingButton) {
        java.awt.event.MouseEvent e = LAST_MOUSE_EVENT;
        if (e == null) {
            return MouseButtonState.Released;
        }
        int mask = swingButton == 1 ? java.awt.event.InputEvent.BUTTON1_DOWN_MASK
                : swingButton == 3 ? java.awt.event.InputEvent.BUTTON3_DOWN_MASK
                : java.awt.event.InputEvent.BUTTON2_DOWN_MASK;
        return (e.getModifiersEx() & mask) != 0 ? MouseButtonState.Pressed : MouseButtonState.Released;
    }

    /** Lets tests inject the last observed Swing mouse event. */
    public static void setLastMouseEvent(java.awt.event.MouseEvent e) {
        LAST_MOUSE_EVENT = e;
    }

    private static java.awt.event.MouseEvent LAST_MOUSE_EVENT;
}
