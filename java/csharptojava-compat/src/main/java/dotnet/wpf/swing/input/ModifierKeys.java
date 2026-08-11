package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.ModifierKeys compatibility flags (mapped to int by the converter).
 */
public final class ModifierKeys {

    private ModifierKeys() {
    }

    public static final int None = 0;
    public static final int Alt = 1;
    public static final int Control = 2;
    public static final int Shift = 4;
    public static final int Windows = 8;
}
