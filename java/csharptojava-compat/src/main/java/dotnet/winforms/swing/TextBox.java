package dotnet.winforms.swing;

/**
 * System.Windows.Forms.TextBox compatibility class.
 */
public class TextBox extends Control {

    private String text;

    public TextBox() {
    }

    public String getText() {
        return text;
    }

    public void setText(String value) {
        text = value;
    }
}
