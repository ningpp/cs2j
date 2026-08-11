package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.RoutedUICommand compatibility class.
 */
public class RoutedUICommand {

    private final String text;
    private final String name;
    private final Class<?> ownerType;

    public RoutedUICommand(String text, String name, Class<?> ownerType) {
        this.text = text;
        this.name = name;
        this.ownerType = ownerType;
    }

    public String getName() {
        return name;
    }

    public String getText() {
        return text;
    }

    public Class<?> getOwnerType() {
        return ownerType;
    }
}
