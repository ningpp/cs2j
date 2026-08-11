package dotnet.winforms.swing;

/**
 * System.Windows.Forms.Button compatibility class.
 */
public class Button extends ButtonBase implements IButtonControl {

    private DialogResult dialogResult = DialogResult.None;

    public Button() {
    }

    @Override
    public DialogResult getDialogResult() {
        return dialogResult;
    }

    @Override
    public void setDialogResult(DialogResult value) {
        dialogResult = value;
    }
}
