package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.UIElement;
import dotnet.wpf.swing.media.Brush;

import java.util.List;

/**
 * System.Windows.Controls.Panel compatibility base class.
 */
public abstract class Panel extends FrameworkElement {

    protected final UIElementCollection children = new UIElementCollection(this);
    private Brush background;

    public Brush getBackground() {
        return background;
    }

    public void setBackground(Brush value) {
        background = value;
    }

    public List<UIElement> getChildren() {
        return children;
    }

    public int getChildrenCount() {
        return children.size();
    }

    public void addChild(UIElement child) {
        children.add(child);
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public void removeChild(UIElement child) {
        children.remove(child);
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public void clearChildren() {
        children.clear();
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    // ---- attached ZIndex (Panel.SetZIndex / Panel.GetZIndex) ----
    private static final java.util.IdentityHashMap<UIElement, Integer> Z_INDEX = new java.util.IdentityHashMap<>();

    public static void setZIndex(UIElement element, int value) {
        if (element != null) {
            Z_INDEX.put(element, value);
        }
    }

    public static int getZIndex(UIElement element) {
        Integer v = element == null ? null : Z_INDEX.get(element);
        return v == null ? 0 : v;
    }
}
