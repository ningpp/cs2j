package dotnet.winforms.swing;

/**
 * System.Windows.Forms.AutoScaleMode compatibility enum.
 */
public enum AutoScaleMode {
    None(0),
    Font(1),
    Dpi(2),
    Inherit(3);

    private final int value;

    AutoScaleMode(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static AutoScaleMode fromValue(int v) {
        for (AutoScaleMode e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static AutoScaleMode fromValueUnchecked(int v) {
        return fromValue(v);
    }
}

