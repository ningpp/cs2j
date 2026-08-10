package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.RoutedEventArgs;

import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

/**
 * System.Windows.Controls.ContextMenu compatibility class.
 */
public class ContextMenu extends FrameworkElement {

    private final List<Object> items = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> closedListeners = new ArrayList<>();

    public List<Object> getItems() {
        return items;
    }

    public void addClosedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        closedListeners.add(handler);
    }

    public void removeClosedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        closedListeners.remove(handler);
    }

    public void raiseClosed() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(closedListeners)) {
            h.accept(this, e);
        }
    }
}
