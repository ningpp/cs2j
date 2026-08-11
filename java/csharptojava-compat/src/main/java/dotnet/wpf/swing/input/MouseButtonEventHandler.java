package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.MouseButtonEventHandler compatibility functional interface.
 */
@FunctionalInterface
public interface MouseButtonEventHandler {

    void accept(Object sender, MouseButtonEventArgs e);
}
