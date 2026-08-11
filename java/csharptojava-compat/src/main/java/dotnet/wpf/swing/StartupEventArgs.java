package dotnet.wpf.swing;

/**
 * System.Windows.StartupEventArgs compatibility class.
 */
public class StartupEventArgs {

    private final String[] args;

    public StartupEventArgs() {
        this(new String[0]);
    }

    public StartupEventArgs(String[] args) {
        this.args = args == null ? new String[0] : args;
    }

    public String[] getArgs() {
        return args;
    }
}
