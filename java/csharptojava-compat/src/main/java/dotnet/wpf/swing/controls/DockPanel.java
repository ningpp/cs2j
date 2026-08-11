package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.UIElement;

import java.util.IdentityHashMap;

/**
 * System.Windows.Controls.DockPanel compatibility class.
 */
public class DockPanel extends Panel {

    private static final IdentityHashMap<UIElement, Object> DOCK = new IdentityHashMap<>();

    public DockPanel() {
    }

    public static Object getDock(UIElement element) {
        return element == null ? null : DOCK.get(element);
    }

    public static void setDock(UIElement element, Object value) {
        if (element != null) {
            DOCK.put(element, value);
        }
    }
}
