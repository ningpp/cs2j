package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.Size;

/**
 * System.Windows.Controls.Primitives.TextBoxBase compatibility class.
 */
public class TextBoxBase extends FrameworkElement {

    @Override
    protected Size measureOverride(Size availableSize) {
        String text = this instanceof TextBox ? ((TextBox) this).getText() : "";
        double width = Math.max(120, text.length() * 8 + 16);
        if (Double.isFinite(availableSize.getWidth()) && availableSize.getWidth() > 0) {
            width = Math.min(width, availableSize.getWidth());
        }
        return new Size(width, 23);
    }

    public void selectAll() {
        if (this instanceof TextBox) {
            TextBox box = (TextBox) this;
            String text = box.getText();
            box.setSelectionStart(0);
            box.setSelectionLength(text == null ? 0 : text.length());
        }
    }
}
