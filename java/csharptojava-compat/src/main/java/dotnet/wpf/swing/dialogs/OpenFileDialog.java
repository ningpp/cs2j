package dotnet.wpf.swing.dialogs;

/** Microsoft.Win32.OpenFileDialog compatibility class. */
public class OpenFileDialog extends FileDialog {

    private boolean restoreDirectory;

    public boolean getRestoreDirectory() {
        return restoreDirectory;
    }

    public void setRestoreDirectory(boolean value) {
        restoreDirectory = value;
    }

    public boolean showDialog() {
        return showChooser(false);
    }
}
