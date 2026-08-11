package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ButtonBase compatibility base class.
 */
public abstract class ButtonBase extends Control {

    private boolean autoSize;
    private String text;
    private boolean useVisualStyleBackColor = true;

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

    public boolean getUseVisualStyleBackColor() {
        return useVisualStyleBackColor;
    }

    public void setUseVisualStyleBackColor(boolean value) {
        useVisualStyleBackColor = value;
    }
}
