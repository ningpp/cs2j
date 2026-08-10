package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.MouseButtonEventArgs compatibility class.
 */
public final class MouseButtonEventArgs extends MouseEventArgs {

    private MouseButton changedButton = MouseButton.Left;
    private MouseButtonState buttonState = MouseButtonState.Released;
    private int clickCount;

    public MouseButton getChangedButton() {
        return changedButton;
    }

    public void setChangedButton(MouseButton value) {
        changedButton = value;
    }

    public MouseButtonState getButtonState() {
        return buttonState;
    }

    public void setButtonState(MouseButtonState value) {
        buttonState = value;
    }

    public int getClickCount() {
        return clickCount;
    }

    public void setClickCount(int value) {
        clickCount = value;
    }
}
