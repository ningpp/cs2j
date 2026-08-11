package dotnet.wpf.swing.controls;

import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.Controls.ItemCollection compatibility class.
 */
public class ItemCollection {

    private final List<Object> items = new ArrayList<>();

    public void add(Object item) {
        items.add(item);
    }

    public int getCount() {
        return items.size();
    }

    public void clear() {
        items.clear();
    }

    public void remove(Object item) {
        items.remove(item);
    }
}
