package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.MouseWheelEventArgs compatibility class.
 */
public final class MouseWheelEventArgs extends MouseEventArgs {

    private int delta;

    public int getDelta() {
        return delta;
    }

    public void setDelta(int value) {
        delta = value;
    }
}
