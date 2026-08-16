package dotnet.wpf.swing;

import dotnet.wpf.swing.input.RoutedUICommand;

/**
 * Local command holder used by {@link XamlLoaderTest} command resolution.
 */
public final class TestCommands {

    public static final RoutedUICommand OpenCommand =
            new RoutedUICommand("Open", "OpenFileCommand", TestCommands.class);

    private TestCommands() {
    }

    public static RoutedUICommand getOpenCommand() {
        return OpenCommand;
    }
}
