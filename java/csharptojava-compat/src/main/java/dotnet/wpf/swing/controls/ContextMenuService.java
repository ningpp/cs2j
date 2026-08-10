package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.UIElement;

import java.util.IdentityHashMap;

/**
 * System.Windows.Controls.ContextMenuService compatibility (static attached-property store).
 */
public final class ContextMenuService {

    private ContextMenuService() {
    }

    private static final IdentityHashMap<UIElement, ContextMenu> CONTEXT_MENUS = new IdentityHashMap<>();

    public static ContextMenu getContextMenu(UIElement element) {
        return element == null ? null : CONTEXT_MENUS.get(element);
    }

    public static void setContextMenu(UIElement element, ContextMenu value) {
        if (element == null) return;
        if (value == null) {
            CONTEXT_MENUS.remove(element);
        } else {
            CONTEXT_MENUS.put(element, value);
        }
    }
}
