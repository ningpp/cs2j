package dotnet.wpf.swing;

import java.util.Objects;

/**
 * System.Windows.FontStyle compatibility value type (normal/italic/oblique).
 */
public final class FontStyle {

    /** Normal style tag. */
    public static final String NORMAL = "Normal";
    public static final String ITALIC = "Italic";
    public static final String OBLIQUE = "Oblique";

    private final String tag;

    public FontStyle() {
        this(NORMAL);
    }

    public FontStyle(String tag) {
        this.tag = tag == null ? NORMAL : tag;
    }

    public String getTag() {
        return tag;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof FontStyle)) return false;
        return tag.equals(((FontStyle) o).tag);
    }

    @Override
    public int hashCode() {
        return Objects.hash(tag);
    }

    @Override
    public String toString() {
        return tag;
    }
}
