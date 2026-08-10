package dotnet.wpf.swing;

import java.util.Objects;

/**
 * System.Windows.FontWeight compatibility value type.
 */
public final class FontWeight {

    private final int weight; // OpenType weight value (100..900)

    public FontWeight() {
        this(400);
    }

    public FontWeight(int weight) {
        this.weight = weight;
    }

    public int getWeight() {
        return weight;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof FontWeight)) return false;
        return weight == ((FontWeight) o).weight;
    }

    @Override
    public int hashCode() {
        return Objects.hash(weight);
    }

    @Override
    public String toString() {
        return "FontWeight(" + weight + ")";
    }
}
