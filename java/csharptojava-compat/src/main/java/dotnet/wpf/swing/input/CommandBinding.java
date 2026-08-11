package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.CommandBinding compatibility class.
 */
public class CommandBinding {

    private final RoutedUICommand command;
    private final ExecutedRoutedEventHandler executed;
    private final CanExecuteRoutedEventHandler canExecute;

    public CommandBinding(RoutedUICommand command, ExecutedRoutedEventHandler executed) {
        this(command, executed, null);
    }

    public CommandBinding(RoutedUICommand command, ExecutedRoutedEventHandler executed,
                          CanExecuteRoutedEventHandler canExecute) {
        this.command = command;
        this.executed = executed;
        this.canExecute = canExecute;
    }

    public RoutedUICommand getCommand() {
        return command;
    }
}
