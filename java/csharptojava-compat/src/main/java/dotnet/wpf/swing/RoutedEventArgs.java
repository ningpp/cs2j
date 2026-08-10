package dotnet.wpf.swing;

/**
 * System.Windows.RoutedEventArgs compatibility base class.
 */
public class RoutedEventArgs {

    private Object source;
    private boolean handled;
    private Object routedEvent;

    public Object getSource() {
        return source;
    }

    public void setSource(Object value) {
        source = value;
    }

    public Object getOriginalSource() {
        return source;
    }

    public void setOriginalSource(Object value) {
        source = value;
    }

    public boolean getHandled() {
        return handled;
    }

    public void setHandled(boolean value) {
        handled = value;
    }

    public Object getRoutedEvent() {
        return routedEvent;
    }

    public void setRoutedEvent(Object value) {
        routedEvent = value;
    }
}
