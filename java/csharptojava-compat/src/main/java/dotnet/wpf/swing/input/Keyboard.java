package dotnet.wpf.swing.input;

import dotnet.wpf.swing.UIElement;

/**
 * System.Windows.Input.Keyboard compatibility static class.
 * Focus changes route through {@link dotnet.wpf.swing.internal.ScenePanel}
 * and raise the real {@code GotFocus}/{@code LostFocus} (and
 * {@code GotKeyboardFocus}/{@code LostKeyboardFocus}) element events.
 */
public final class Keyboard {

    private Keyboard() {
    }

    private static Object focusedElement;

    public static int getModifiers() {
        // Swing has no reliable global modifier state; return None for the compat layer.
        return ModifierKeys.None;
    }

    public static Object getFocusedElement() {
        return focusedElement;
    }

    /** Moves keyboard focus to the element, raising focus lifecycle events. */
    public static Object focus(Object element) {
        Object previous = focusedElement;
        if (previous == element) {
            if (element instanceof UIElement) {
                dotnet.wpf.swing.internal.ScenePanel.requestFocusOn((UIElement) element);
            }
            return element;
        }
        focusedElement = element;
        if (previous instanceof UIElement) {
            ((UIElement) previous).raiseLostFocus();
        }
        if (element instanceof UIElement) {
            UIElement ue = (UIElement) element;
            ue.raiseGotFocus();
            dotnet.wpf.swing.internal.ScenePanel.requestFocusOn(ue);
        }
        return element;
    }

    /** Clears keyboard focus, raising LostFocus on the focused element. */
    public static void clearFocus() {
        Object previous = focusedElement;
        focusedElement = null;
        if (previous instanceof UIElement) {
            ((UIElement) previous).raiseLostFocus();
        }
    }

    public static boolean isKeyDown(Key key) {
        // Swing has no reliable global key state; default to false.
        return false;
    }

    public static boolean isKeyUp(Key key) {
        return !isKeyDown(key);
    }

    public static boolean isKeyToggled(Key key) {
        return false;
    }
}