package dotnet.wpf.swing.controls;

/**
 * System.Windows.Controls.TextBox compatibility class.
 */
public class TextBox extends TextBoxBase {

    private String text = "";
    private int caretIndex;
    private int selectionStart;
    private int selectionLength;

    public String getText() {
        return text;
    }

    public void setText(String value) {
        text = value == null ? "" : value;
        if (caretIndex > text.length()) {
            caretIndex = text.length();
        }
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public int getCaretIndex() {
        return caretIndex;
    }

    public void setCaretIndex(int value) {
        caretIndex = value;
    }

    public int getSelectionStart() {
        return selectionStart;
    }

    public void setSelectionStart(int value) {
        selectionStart = value;
    }

    public int getSelectionLength() {
        return selectionLength;
    }

    public void setSelectionLength(int value) {
        selectionLength = value;
    }
}
