package dotnet.wpf.swing;

/**
 * System.Windows.Application compatibility class.
 * Generated code subclasses this (e.g. {@code class App extends Application}).
 */
public class Application {

    private static Application current;
    private Window mainWindow;

    public Application() {
        current = this;
    }

    public int run() {
        return 0;
    }

    protected void onStartup(StartupEventArgs e) {
        // overridable hook; no-op in the Swing-backed compat layer
    }

    public void shutdown() {
        // no-op
    }

    public Window getMainWindow() {
        return mainWindow;
    }

    public void setMainWindow(Window value) {
        mainWindow = value;
    }

    public static Application getCurrent() {
        return current;
    }

    public static void loadComponent(Window window, dotnet.system.Uri resourceLocator) {
        // no-op in the Swing-backed compat layer
    }
}
