package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.RoutedEventArgs;
import dotnet.wpf.swing.RoutedEventHandler;
import dotnet.wpf.swing.Size;

import javax.swing.JLabel;
import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.Controls.MenuItem compatibility class.
 */
public class MenuItem extends FrameworkElement {

    private Object header;
    private Object command;
    private final ItemCollection items = new ItemCollection();
    private final List<RoutedEventHandler> clickListeners = new ArrayList<>();

    public Object getHeader() {
        return header;
    }

    public void setHeader(Object value) {
        header = value;
    }

    public ItemCollection getItems() {
        return items;
    }

    public Object getCommand() {
        return command;
    }

    public void setCommand(Object value) {
        command = value;
    }

    @Override
    protected Size measureOverride(Size availableSize) {
        String text = header == null ? "" : String.valueOf(header);
        java.awt.Dimension preferred = new JLabel(text).getPreferredSize();
        double width = preferred.getWidth() + 14;
        double height = Math.max(22, preferred.getHeight() + 8);
        if (Double.isFinite(availableSize.getWidth()) && availableSize.getWidth() > 0) {
            width = Math.min(width, availableSize.getWidth());
        }
        return new Size(width, height);
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
