package dotnet.winforms.swing;

/**
 * System.Windows.Forms.KeyPressEventArgs compatibility class.
 */
public class KeyPressEventArgs {

    private char keyChar;

    public char getKeyChar() {
        return keyChar;
    }

    public void setKeyChar(char value) {
        keyChar = value;
    }
}
