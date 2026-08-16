package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ToolStripMenuItem compatibility class.
 */
public class ToolStripMenuItem extends ToolStripItem {

    private final ToolStrip.ToolStripItemCollection dropDownItems = new ToolStrip.ToolStripItemCollection();
    private int shortcutKeys;

    public ToolStripMenuItem() {
    }

    public ToolStripMenuItem(String text) {
        setText(text);
    }

    public ToolStrip.ToolStripItemCollection getDropDownItems() {
        return dropDownItems;
    }

    public int getShortcutKeys() {
        return shortcutKeys;
    }

    public void setShortcutKeys(int value) {
        shortcutKeys = value;
    }
}
