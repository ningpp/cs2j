package dotnet.wpf.swing;

/**
 * System.Windows.DependencyProperty compatibility class (marker).
 */
public class DependencyProperty {

    public static final Object UnsetValue = new Object();

    private final String name;

    private DependencyProperty(String name) {
        this.name = name;
    }

    public static DependencyProperty register(String name, Class<?> propertyType, Class<?> ownerType) {
        return new DependencyProperty(name);
    }

    public String getName() {
        return name;
    }
}
