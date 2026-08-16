package dotnet.wpf.swing.dialogs;

/** Microsoft.Win32.SaveFileDialog compatibility class. */
public class SaveFileDialog extends FileDialog {

    public boolean showDialog() {
        return showChooser(true);
    }
}
