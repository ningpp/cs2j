package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.MouseButtonState compatibility enum.
 */
public enum MouseButtonState {
    Released(0),
    Pressed(1);

    private final int value;

    MouseButtonState(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static MouseButtonState fromValue(int value) {
        for (MouseButtonState e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static MouseButtonState fromValueUnchecked(int value) {
        for (MouseButtonState e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}

