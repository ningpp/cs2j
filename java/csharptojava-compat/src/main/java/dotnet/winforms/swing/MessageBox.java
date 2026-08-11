package dotnet.winforms.swing;

/**
 * System.Windows.Forms.MessageBox compatibility class.
 */
public final class MessageBox {

    private MessageBox() {
    }

    public static DialogResult show(String text) {
        return DialogResult.OK;
    }

    public static DialogResult show(String text, String caption) {
        return DialogResult.OK;
    }
}
