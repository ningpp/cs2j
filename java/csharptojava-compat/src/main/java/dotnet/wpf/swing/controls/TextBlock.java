package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.media.Brush;
import dotnet.wpf.swing.media.FontFamily;
import dotnet.wpf.swing.media.FormattedText;
import dotnet.wpf.swing.media.Typeface;
import dotnet.wpf.swing.FontStretches;
import dotnet.wpf.swing.FontStyle;
import dotnet.wpf.swing.FontWeights;
import dotnet.wpf.swing.Size;

/**
 * System.Windows.Controls.TextBlock compatibility class.
 */
public class TextBlock extends FrameworkElement {

    private String text = "";
    private FontFamily fontFamily = new FontFamily(java.awt.Font.DIALOG);
    private double fontSize = 12;
    private FontStyle fontStyle = new FontStyle();
    private dotnet.wpf.swing.FontWeight fontWeight = FontWeights.Normal;
    private dotnet.wpf.swing.FontStretch fontStretch = FontStretches.Normal;
    private Brush foreground;
    private Brush background;

    public String getText() {
        return text;
    }

    public void setText(String value) {
        this.text = value == null ? "" : value;
    }

    public FontFamily getFontFamily() {
        return fontFamily;
    }

    public void setFontFamily(FontFamily value) {
        fontFamily = value == null ? new FontFamily(java.awt.Font.DIALOG) : value;
    }

    public double getFontSize() {
        return fontSize;
    }

    public void setFontSize(double value) {
        fontSize = value;
    }

    public FontStyle getFontStyle() {
        return fontStyle;
    }

    public void setFontStyle(FontStyle value) {
        fontStyle = value;
    }

    public dotnet.wpf.swing.FontWeight getFontWeight() {
        return fontWeight;
    }

    public void setFontWeight(dotnet.wpf.swing.FontWeight value) {
        fontWeight = value;
    }

    public dotnet.wpf.swing.FontStretch getFontStretch() {
        return fontStretch;
    }

    public void setFontStretch(dotnet.wpf.swing.FontStretch value) {
        fontStretch = value;
    }

    public Brush getForeground() {
        return foreground;
    }

    public void setForeground(Brush value) {
        foreground = value;
    }

    public Brush getBackground() {
        return background;
    }

    public void setBackground(Brush value) {
        background = value;
    }

    @Override
    protected Size measureOverride(Size availableSize) {
        if (text == null || text.isEmpty()) {
            return new Size(0, 0);
        }
        Typeface tf = new Typeface(fontFamily, fontStyle, fontWeight, fontStretch);
        FormattedText ft = new FormattedText(text, new Object(), dotnet.wpf.swing.FlowDirection.LeftToRight,
                tf, fontSize, foreground == null ? dotnet.wpf.swing.media.Brushes.Black : foreground);
        double w = Double.isNaN(getWidth()) ? ft.getWidth() : getWidth();
        double h = Double.isNaN(getHeight()) ? ft.getHeight() : getHeight();
        return new Size(w, h);
    }
}
