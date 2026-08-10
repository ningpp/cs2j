package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.CornerRadius;
import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.Thickness;
import dotnet.wpf.swing.UIElement;
import dotnet.wpf.swing.media.Brush;

/**
 * System.Windows.Controls.Border compatibility class.
 */
public class Border extends FrameworkElement {

    private Brush background;
    private Brush borderBrush;
    private Thickness borderThickness;
    private CornerRadius cornerRadius;
    private Thickness padding;
    private UIElement child;

    public Brush getBackground() {
        return background;
    }

    public void setBackground(Brush value) {
        background = value;
    }

    public Brush getBorderBrush() {
        return borderBrush;
    }

    public void setBorderBrush(Brush value) {
        borderBrush = value;
    }

    public Thickness getBorderThickness() {
        return borderThickness;
    }

    public void setBorderThickness(Thickness value) {
        borderThickness = value;
    }

    public CornerRadius getCornerRadius() {
        return cornerRadius;
    }

    public void setCornerRadius(CornerRadius value) {
        cornerRadius = value;
    }

    public Thickness getPadding() {
        return padding;
    }

    public void setPadding(Thickness value) {
        padding = value;
    }

    public UIElement getChild() {
        return child;
    }

    public void setChild(UIElement value) {
        this.child = value;
        if (value != null) {
            addVisualChild(value);
        }
    }
}
