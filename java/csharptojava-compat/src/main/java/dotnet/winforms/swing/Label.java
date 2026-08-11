package dotnet.winforms.swing;

/**
 * System.Windows.Forms.Label compatibility class.
 */
public class Label extends Control {

    private boolean autoSize;
    private String text;

    public Label() {
    }

    public boolean getAutoSize() {
        return autoSize;
    }

    public void setAutoSize(boolean value) {
        autoSize = value;
    }

    public String getText() {
        return text;
    }

    public void setText(String value) {
        text = value;
    }
}
