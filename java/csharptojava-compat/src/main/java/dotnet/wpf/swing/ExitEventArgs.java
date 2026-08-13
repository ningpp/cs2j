package dotnet.wpf.swing;

import io.github.ningpp.compat.EventArgs;

/**
 * System.Windows.ExitEventArgs compatibility class.
 */
public class ExitEventArgs extends EventArgs {

    private int exitCode;

    public ExitEventArgs() {
    }

    public ExitEventArgs(int exitCode) {
        this.exitCode = exitCode;
    }

    public int getExitCode() {
        return exitCode;
    }

    public void setExitCode(int value) {
        exitCode = value;
    }
}