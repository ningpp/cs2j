package dotnet.drawing.swing.printing;

import dotnet.drawing.swing.Graphics;
import dotnet.drawing.swing.Rectangle;

/**
 * System.Drawing.Printing.PrintPageEventArgs compatibility class.
 */
public class PrintPageEventArgs {

    public Graphics getGraphics() {
        return new Graphics(null);
    }

    public Rectangle getPageBounds() {
        return new Rectangle(0, 0, 0, 0);
    }
}