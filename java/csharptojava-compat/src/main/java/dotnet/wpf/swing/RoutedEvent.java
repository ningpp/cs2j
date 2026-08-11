package dotnet.wpf.swing;

/**
 * System.Windows.RoutedEvent compatibility marker class.
 */
public class RoutedEvent {

    private final String name;

    public RoutedEvent(String name) {
        this.name = name;
    }

    public String getName() {
        return name;
    }
}
