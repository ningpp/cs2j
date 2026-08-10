package dotnet.wpf.swing.input;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.RoutedEventArgs;

/**
 * System.Windows.Input.MouseEventArgs compatibility base class.
 * Carries Swing mouse coordinates; LeftButton/RightButton/etc. state via MouseButtonState.
 */
public class MouseEventArgs extends RoutedEventArgs {

    private MouseButtonState leftButton = MouseButtonState.Released;
    private MouseButtonState rightButton = MouseButtonState.Released;
    private MouseButtonState middleButton = MouseButtonState.Released;
    private Point position = new Point();

    public MouseButtonState getLeftButton() {
        return leftButton;
    }

    public void setLeftButton(MouseButtonState value) {
        leftButton = value;
    }

    public MouseButtonState getRightButton() {
        return rightButton;
    }

    public void setRightButton(MouseButtonState value) {
        rightButton = value;
    }

    public MouseButtonState getMiddleButton() {
        return middleButton;
    }

    public void setMiddleButton(MouseButtonState value) {
        middleButton = value;
    }

    public Point getPosition() {
        return position;
    }

    public void setPosition(Point value) {
        position = value;
    }

    public Point getPosition(Object relativeTo) {
        return position;
    }
}
