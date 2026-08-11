package dotnet.winforms.swing;

import dotnet.drawing.swing.Point;

/**
 * System.Windows.Forms.MouseEventArgs compatibility class.
 */
public class MouseEventArgs {

    private final int button;
    private final int clicks;
    private final int x;
    private final int y;
    private final int delta;

    public MouseEventArgs(int button, int clicks, int x, int y, int delta) {
        this.button = button;
        this.clicks = clicks;
        this.x = x;
        this.y = y;
        this.delta = delta;
    }

    public int getButton() {
        return button;
    }

    public int getClicks() {
        return clicks;
    }

    public int getDelta() {
        return delta;
    }

    public Point getLocation() {
        return new Point(x, y);
    }

    public int getX() {
        return x;
    }

    public int getY() {
        return y;
    }
}
