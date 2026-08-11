package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.ExecutedRoutedEventHandler compatibility functional interface.
 */
@FunctionalInterface
public interface ExecutedRoutedEventHandler {

    void accept(Object sender, ExecutedRoutedEventArgs e);
}
