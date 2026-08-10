package dotnet.wpf.swing.input;

/**
 * System.Windows.IInputElement compatibility interface.
 */
public interface IInputElement {
    boolean isFocusable();
    void setFocusable(boolean value);
    boolean isEnabled();
    boolean isKeyboardFocused();
    boolean isKeyboardFocusWithin();
    boolean isMouseCaptured();
    boolean isMouseDirectlyOver();
    boolean isMouseOver();
}
