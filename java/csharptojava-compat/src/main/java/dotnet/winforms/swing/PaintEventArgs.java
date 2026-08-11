package dotnet.winforms.swing;

import dotnet.drawing.swing.Graphics;
import dotnet.drawing.swing.Rectangle;

/**
 * System.Windows.Forms.PaintEventArgs compatibility class.
 */
public class PaintEventArgs {

    private final Graphics graphics;
    private final Rectangle clipRectangle;

    public PaintEventArgs(Graphics graphics, Rectangle clipRectangle) {
        this.graphics = graphics;
        this.clipRectangle = clipRectangle;
    }

    public Graphics getGraphics() {
        return graphics;
    }

    public Rectangle getClipRectangle() {
        return clipRectangle;
    }
}
