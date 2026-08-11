package dotnet.winforms.swing;

import dotnet.drawing.swing.Point;

/**
 * System.Windows.Forms.ToolStripDropDown compatibility class.
 */
public class ToolStripDropDown extends ToolStrip {

    public void show(Point location) {
        // no-op in the Swing-backed compat layer
    }

    public void show(Control owner, Point location) {
        // no-op in the Swing-backed compat layer
    }

    public void show(Control owner, int x, int y) {
        show(owner, new Point(x, y));
    }

    public void show(Control owner, Point location, ToolStripDropDownDirection direction) {
        // no-op in the Swing-backed compat layer
    }
}
