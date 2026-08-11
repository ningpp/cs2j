package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.MouseButton compatibility enum.
 */
public enum MouseButton {
    Left(0),
    Middle(1),
    Right(2),
    XButton1(3),
    XButton2(4);

    private final int value;

    MouseButton(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static MouseButton fromValue(int value) {
        for (MouseButton e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static MouseButton fromValueUnchecked(int value) {
        for (MouseButton e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}

