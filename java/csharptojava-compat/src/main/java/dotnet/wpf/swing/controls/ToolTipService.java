package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.UIElement;

import java.util.IdentityHashMap;

/**
 * System.Windows.Controls.ToolTipService compatibility (static attached-property store).
 */
public final class ToolTipService {

    private ToolTipService() {
    }

    private static final IdentityHashMap<UIElement, Boolean> IS_ENABLED = new IdentityHashMap<>();

    public static void setIsEnabled(UIElement element, boolean value) {
        if (element != null) {
            IS_ENABLED.put(element, value);
        }
    }

    public static boolean getIsEnabled(UIElement element) {
        Boolean v = element == null ? null : IS_ENABLED.get(element);
        return v == null || v;
    }
}
