package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.InputBinding compatibility class.
 */
public class InputBinding {

    private final RoutedUICommand command;
    private final KeyGesture gesture;

    public InputBinding(RoutedUICommand command, KeyGesture gesture) {
        this.command = command;
        this.gesture = gesture;
    }

    public RoutedUICommand getCommand() {
        return command;
    }

    public KeyGesture getGesture() {
        return gesture;
    }
}
