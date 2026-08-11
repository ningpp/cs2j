package dotnet.wpf.swing.dialogs;

/** Microsoft.Win32.FileDialog compatibility class. */
public class FileDialog extends CommonItemDialog {

    private String fileName;
    private String defaultExt;
    private String filter;

    public String getFileName() {
        return fileName;
    }

    public void setFileName(String value) {
        fileName = value;
    }

    public String getDefaultExt() {
        return defaultExt;
    }

    public void setDefaultExt(String value) {
        defaultExt = value;
    }

    public String getFilter() {
        return filter;
    }

    public void setFilter(String value) {
        filter = value;
    }
}
