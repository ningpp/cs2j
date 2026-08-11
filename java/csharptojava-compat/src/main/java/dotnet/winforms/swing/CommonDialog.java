package dotnet.winforms.swing;

/**
 * System.Windows.Forms.CommonDialog compatibility base class.
 */
public abstract class CommonDialog {

    public DialogResult showDialog() {
        return DialogResult.OK;
    }
}
