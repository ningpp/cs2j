package dotnet.wpf.swing.media;

import dotnet.wpf.swing.FontStretch;
import dotnet.wpf.swing.FontStyle;
import dotnet.wpf.swing.FontWeight;

import java.util.Objects;

/**
 * System.Windows.Media.Typeface compatibility class.
 */
public final class Typeface {

    private final FontFamily fontFamily;
    private final FontStyle style;
    private final FontWeight weight;
    private final FontStretch stretch;

    public Typeface(String typefaceName) {
        this(new FontFamily(typefaceName), new FontStyle(), dotnet.wpf.swing.FontWeights.Normal,
                dotnet.wpf.swing.FontStretches.Normal);
    }

    public Typeface(FontFamily fontFamily, FontStyle style, FontWeight weight, FontStretch stretch) {
        this.fontFamily = fontFamily;
        this.style = style;
        this.weight = weight;
        this.stretch = stretch;
    }

    public FontFamily getFontFamily() {
        return fontFamily;
    }

    public FontStyle getStyle() {
        return style;
    }

    public FontWeight getWeight() {
        return weight;
    }

    public FontStretch getStretch() {
        return stretch;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Typeface)) return false;
        Typeface that = (Typeface) o;
        return Objects.equals(fontFamily, that.fontFamily)
                && Objects.equals(style, that.style)
                && Objects.equals(weight, that.weight)
                && Objects.equals(stretch, that.stretch);
    }

    @Override
    public int hashCode() {
        return Objects.hash(fontFamily, style, weight, stretch);
    }
}
