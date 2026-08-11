package dotnet.winforms.swing;

/**
 * System.Windows.Forms.MouseEventHandler compatibility functional interface.
 */
@FunctionalInterface
public interface MouseEventHandler {

    void accept(Object sender, MouseEventArgs e);
}
