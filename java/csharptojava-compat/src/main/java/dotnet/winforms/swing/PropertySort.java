package dotnet.winforms.swing;

/**
 * System.Windows.Forms.PropertySort compatibility enum.
 */
public enum PropertySort {
    NoSort(0),
    Alphabetical(1),
    Categorized(2),
    CategorizedAlphabetical(3);

    private final int value;

    PropertySort(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static PropertySort fromValue(int v) {
        for (PropertySort e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static PropertySort fromValueUnchecked(int v) {
        return fromValue(v);
    }
}

