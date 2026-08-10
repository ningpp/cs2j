package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.RoutedEventArgs;

import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

/**
 * System.Windows.Controls.MenuItem compatibility class.
 */
public class MenuItem extends FrameworkElement {

    private Object header;
    private final List<BiConsumer<Object, RoutedEventArgs>> clickListeners = new ArrayList<>();

    public Object getHeader() {
        return header;
    }

    public void setHeader(Object value) {
        header = value;
    }

    public void addClickListener(BiConsumer<Object, RoutedEventArgs> handler) {
        clickListeners.add(handler);
    }

    public void removeClickListener(BiConsumer<Object, RoutedEventArgs> handler) {
        clickListeners.remove(handler);
    }

    public void raiseClick() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(clickListeners)) {
            h.accept(this, e);
        }
    }
}
