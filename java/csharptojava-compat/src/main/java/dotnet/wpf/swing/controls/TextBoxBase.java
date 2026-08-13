package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;

/**
 * System.Windows.Controls.Primitives.TextBoxBase compatibility class.
 */
public class TextBoxBase extends FrameworkElement {

    public void selectAll() {
        if (this instanceof TextBox) {
            TextBox box = (TextBox) this;
            String text = box.getText();
            box.setSelectionStart(0);
            box.setSelectionLength(text == null ? 0 : text.length());
        }
    }
}