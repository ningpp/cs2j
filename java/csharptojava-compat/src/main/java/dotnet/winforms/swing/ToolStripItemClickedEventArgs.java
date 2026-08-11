package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ToolStripItemClickedEventArgs compatibility class.
 */
public class ToolStripItemClickedEventArgs {

    private final ToolStripItem clickedItem;

    public ToolStripItemClickedEventArgs(ToolStripItem clickedItem) {
        this.clickedItem = clickedItem;
    }

    public ToolStripItem getClickedItem() {
        return clickedItem;
    }
}
