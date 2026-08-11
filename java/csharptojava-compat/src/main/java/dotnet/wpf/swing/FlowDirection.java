package dotnet.wpf.swing;

/**
 * System.Windows.FlowDirection compatibility enum.
 */
public enum FlowDirection {
    LeftToRight(0),
    RightToLeft(1);

    private final int value;

    FlowDirection(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static FlowDirection fromValue(int value) {
        for (FlowDirection e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static FlowDirection fromValueUnchecked(int value) {
        for (FlowDirection e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}

