package dotnet.wpf.swing;

/**
 * System.Windows.Controls.ContentControl compatibility base class (single Content property).
 */
public class ContentControl extends FrameworkElement {

    private Object content;

    public Object getContent() {
        return content;
    }

    public void setContent(Object value) {
        this.content = value;
        if (value instanceof UIElement) {
            addVisualChild((UIElement) value);
        }
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }
}
