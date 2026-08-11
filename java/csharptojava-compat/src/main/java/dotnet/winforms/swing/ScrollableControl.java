package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ScrollableControl compatibility base class.
 */
public class ScrollableControl extends Control {

    private boolean autoScroll;

    public boolean getAutoScroll() {
        return autoScroll;
    }

    public void setAutoScroll(boolean value) {
        autoScroll = value;
    }
}
