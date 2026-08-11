package dotnet.wpf.swing;

/**
 * System.Windows.RoutedEventHandler compatibility functional interface.
 */
@FunctionalInterface
public interface RoutedEventHandler {

    void accept(Object sender, RoutedEventArgs e);
}
