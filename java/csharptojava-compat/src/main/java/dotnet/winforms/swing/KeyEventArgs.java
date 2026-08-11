package dotnet.winforms.swing;

/**
 * System.Windows.Forms.KeyEventArgs compatibility class.
 */
public class KeyEventArgs {

    private final int keyData;

    public KeyEventArgs(int keyData) {
        this.keyData = keyData;
    }

    public int getKeyData() {
        return keyData;
    }
}
