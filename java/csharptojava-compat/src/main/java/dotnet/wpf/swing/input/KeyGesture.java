package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.KeyGesture compatibility class.
 */
public class KeyGesture {

    private final Key key;
    private final int modifiers;

    public KeyGesture(Key key) {
        this(key, ModifierKeys.None);
    }

    public KeyGesture(Key key, int modifiers) {
        this.key = key;
        this.modifiers = modifiers;
    }

    public Key getKey() {
        return key;
    }

    public int getModifiers() {
        return modifiers;
    }
}
