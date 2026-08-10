package dotnet.wpf.swing;

import java.util.Objects;

/**
 * System.Windows.FontStretch compatibility value type.
 */
public final class FontStretch {

    private final int stretch; // OpenType stretch value (1..9), 5 = normal

    public FontStretch() {
        this(5);
    }

    public FontStretch(int stretch) {
        this.stretch = stretch;
    }

    public int getStretch() {
        return stretch;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof FontStretch)) return false;
        return stretch == ((FontStretch) o).stretch;
    }

    @Override
    public int hashCode() {
        return Objects.hash(stretch);
    }

    @Override
    public String toString() {
        return "FontStretch(" + stretch + ")";
    }
}
