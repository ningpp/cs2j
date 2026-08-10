package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.Keyboard compatibility static class.
 */
public final class Keyboard {

    private Keyboard() {
    }

    private static Object focusedElement;

    public static ModifierKeys getModifiers() {
        // Swing has no reliable global modifier state; return None for the compat layer.
        return ModifierKeys.None;
    }

    public static Object getFocusedElement() {
        return focusedElement;
    }

    public static Object focus(Object element) {
        focusedElement = element;
        return element;
    }

    public static void clearFocus() {
        focusedElement = null;
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
