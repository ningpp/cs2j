package dotnet.wpf.swing;

/**
 * System.Windows.Window compatibility class (Swing-backed no-op shell).
 */
public class Window extends ContentControl {

    private String title = "";

    public String getTitle() {
        return title;
    }

    public void setTitle(String value) {
        title = value == null ? "" : value;
    }

    public void show() {
        // no-op: no real window in the Swing-backed compat layer
    }

    public void close() {
        // no-op
    }

    public void initializeComponent() {
        // no-op
    }
}
