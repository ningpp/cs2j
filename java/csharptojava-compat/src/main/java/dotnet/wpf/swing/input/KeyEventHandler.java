package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.KeyEventHandler compatibility functional interface.
 */
@FunctionalInterface
public interface KeyEventHandler {

    void accept(Object sender, KeyEventArgs e);
}
