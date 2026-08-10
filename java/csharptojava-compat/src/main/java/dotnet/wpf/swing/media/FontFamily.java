package dotnet.wpf.swing.media;

import java.util.Objects;

/**
 * System.Windows.Media.FontFamily compatibility class.
 */
public class FontFamily {

    private final String source;

    public FontFamily() {
        this("");
    }

    public FontFamily(String familyName) {
        this.source = familyName == null ? "" : familyName;
    }

    public String getSource() {
        return source;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof FontFamily)) return false;
        return source.equals(((FontFamily) o).source);
    }

    @Override
    public int hashCode() {
        return Objects.hash(source);
    }

    @Override
    public String toString() {
        return source;
    }
}
