package dotnet.wpf.swing.input;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.UIElement;

/**
 * System.Windows.Input.Mouse compatibility static class.
 * Swing backend tracks the last known cursor position; capture changes
 * raise the real {@code GotMouseCapture}/{@code LostMouseCapture} element
 * events.
 */
public final class Mouse {

    private Mouse() {
    }

    private static boolean captured;
    private static Object capturedElement;

    public static Point getPosition(Object relativeTo) {
        java.awt.Point p = new java.awt.Point();
        try {
            java.awt.PointerInfo info = java.awt.MouseInfo.getPointerInfo();
            if (info != null) {
                p = info.getLocation();
            }
        } catch (java.awt.HeadlessException ignored) {
            // no pointer in headless environments; report the origin
        }
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

    /** Captures the mouse to the element, raising capture lifecycle events. */
    public static boolean capture(Object element) {
        Object previous = capturedElement;
        if (previous == element && captured) {
            return true;
        }
        if (previous instanceof UIElement) {
            ((UIElement) previous).releaseMouseCapture();
        }
        capturedElement = element;
        captured = true;
        if (element instanceof UIElement && !((UIElement) element).getIsMouseCaptured()) {
            ((UIElement) element).captureMouse();
        }
        return true;
    }

    /** Releases an active mouse capture, raising LostMouseCapture. */
    public static void unCapture() {
        if (!captured) {
            return;
        }
        if (capturedElement instanceof UIElement) {
            ((UIElement) capturedElement).releaseMouseCapture();
        }
        capturedElement = null;
        captured = false;
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