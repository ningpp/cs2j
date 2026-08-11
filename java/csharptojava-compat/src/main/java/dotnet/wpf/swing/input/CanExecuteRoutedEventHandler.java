package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.CanExecuteRoutedEventHandler compatibility functional interface.
 */
@FunctionalInterface
public interface CanExecuteRoutedEventHandler {

    void accept(Object sender, ExecutedRoutedEventArgs e);
}
