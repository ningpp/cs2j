package dotnet.wpf.swing;

/**
 * System.Windows.EventManager compatibility class (registration is a no-op).
 */
public final class EventManager {

    private EventManager() {
    }

    public static void registerClassHandler(Class<?> ownerType, RoutedEvent routedEvent,
                                            RoutedEventHandler handler) {
        // no-op
    }

    public static void registerClassHandler(Class<?> ownerType, RoutedEvent routedEvent,
                                            RoutedEventHandler handler, boolean handledEventsToo) {
        // no-op
    }

    public static void registerClassHandler(Class<?> ownerType, RoutedEvent routedEvent,
                                            dotnet.wpf.swing.input.MouseButtonEventHandler handler, boolean handledEventsToo) {
        // no-op
    }
}
