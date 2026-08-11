package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ToolStripItemClickedEventHandler compatibility functional interface.
 */
@FunctionalInterface
public interface ToolStripItemClickedEventHandler {

    void accept(Object sender, ToolStripItemClickedEventArgs e);
}