package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.RoutedEventArgs;
import dotnet.wpf.swing.RoutedEventHandler;

import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.Controls.MenuItem compatibility class.
 */
public class MenuItem extends FrameworkElement {

    private Object header;
    private final List<RoutedEventHandler> clickListeners = new ArrayList<>();

    public Object getHeader() {
        return header;
    }

    public void setHeader(Object value) {
        header = value;
    }

    public void addClickListener(RoutedEventHandler handler) {
        clickListeners.add(handler);
    }

    public void removeClickListener(RoutedEventHandler handler) {
        clickListeners.remove(handler);
    }

    public void raiseClick() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (RoutedEventHandler h : new ArrayList<>(clickListeners)) {
            h.accept(this, e);
        }
    }
}
