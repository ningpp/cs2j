package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.Size;
import dotnet.wpf.swing.UIElement;

/**
 * System.Windows.Controls.ToolBar compatibility class.
 */
public class ToolBar extends Panel {

    @Override
    protected Size measureOverride(Size availableSize) {
        double height = 28;
        double width = 0;
        for (UIElement child : getChildren()) {
            if (child == null) {
                continue;
            }
            child.measure(new Size(Double.POSITIVE_INFINITY, height));
            Size d = child.getDesiredSize();
            if (d != null && d.getWidth() > 0) {
                width += d.getWidth();
            }
        }
        double availableWidth = Double.isFinite(availableSize.getWidth())
                ? availableSize.getWidth() : width;
        return new Size(availableWidth > 0 ? availableWidth : width, height);
    }
}
