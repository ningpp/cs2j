package dotnet.winforms.swing;

/**
 * System.Windows.Forms.FileDialog compatibility base class.
 */
public abstract class FileDialog extends CommonDialog {

    private String fileName;
    private String filter;
    private boolean restoreDirectory;

    public String getFileName() {
        return fileName;
    }

    public void setFileName(String value) {
        fileName = value;
    }

    public String getFilter() {
        return filter;
    }

    public void setFilter(String value) {
        filter = value;
    }

    public boolean getRestoreDirectory() {
        return restoreDirectory;
    }

    public void setRestoreDirectory(boolean value) {
        restoreDirectory = value;
    }
}
