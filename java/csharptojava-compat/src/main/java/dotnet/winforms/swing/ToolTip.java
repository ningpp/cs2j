package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ToolTip compatibility class.
 */
public class ToolTip {

    public ToolTip() {
    }

    public ToolTip(IContainer container) {
    }

    public void removeAll() {
        // no-op in the Swing-backed compat layer
    }

    public void setToolTip(Control control, String caption) {
        // no-op in the Swing-backed compat layer
    }

    public void close() {
        // no-op in the Swing-backed compat layer
    }
}
