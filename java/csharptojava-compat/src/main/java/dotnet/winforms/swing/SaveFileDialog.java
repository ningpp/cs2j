package dotnet.winforms.swing;

/**
 * System.Windows.Forms.SaveFileDialog compatibility class.
 */
public class SaveFileDialog extends FileDialog {

    private boolean overwritePrompt = true;

    public SaveFileDialog() {
    }

    public boolean getOverwritePrompt() {
        return overwritePrompt;
    }

    public void setOverwritePrompt(boolean value) {
        overwritePrompt = value;
    }
}
