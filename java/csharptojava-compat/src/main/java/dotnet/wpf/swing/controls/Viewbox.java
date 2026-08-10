package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.UIElement;
import dotnet.wpf.swing.media.Stretch;

/**
 * System.Windows.Controls.Viewbox compatibility class.
 */
public class Viewbox extends FrameworkElement {

    private UIElement child;
    private Stretch stretch = Stretch.Uniform;

    public UIElement getChild() {
        return child;
    }

    public void setChild(UIElement value) {
        this.child = value;
        if (value != null) {
            addVisualChild(value);
        }
    }

    public Stretch getStretch() {
        return stretch;
    }

    public void setStretch(Stretch value) {
        stretch = value;
    }
}
