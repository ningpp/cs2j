package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.Stretch compatibility enum.
 */
public enum Stretch {
    None(0),
    Fill(1),
    Uniform(2),
    UniformToFill(3);

    private final int value;

    Stretch(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static Stretch fromValue(int value) {
        for (Stretch e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static Stretch fromValueUnchecked(int value) {
        for (Stretch e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
