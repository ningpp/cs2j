package dotnet.wpf.swing.controls;

/**
 * System.Windows.Controls.RichTextBox compatibility class.
 */
public class RichTextBox extends TextBoxBase {

    private String text = "";

    public RichTextBox() {
    }

    public String getText() {
        return text;
    }

    public void setText(String value) {
        text = value == null ? "" : value;
    }
}
