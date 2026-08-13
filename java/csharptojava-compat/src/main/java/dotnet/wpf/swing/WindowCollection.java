package dotnet.wpf.swing;

import java.util.AbstractList;
import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.WindowCollection compatibility class.
 * A read-only snapshot of the application's open windows (populated by
 * {@link Application#getWindows()}).
 */
public final class WindowCollection extends AbstractList<Window> {

    private final List<Window> windows;

    WindowCollection(List<Window> windows) {
        this.windows = new ArrayList<>(windows);
    }

    @Override
    public Window get(int index) {
        return windows.get(index);
    }

    @Override
    public int size() {
        return windows.size();
    }
}