package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.UIElement;

import java.util.AbstractList;
import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

/**
 * System.Windows.Controls.UIElementCollection compatibility class.
 *
 * <p>WPF panel children are a live collection: adding an element registers it
 * as a visual child of the owning panel and removing it clears that parent.
 * Generated C# code writes {@code panel.Children.Add(...)}, which the
 * converter emits as {@code panel.getChildren().add(...)}; this collection
 * keeps those direct list mutations equivalent to {@code Panel.addChild}.</p>
 */
public final class UIElementCollection extends AbstractList<UIElement> {

    private final Panel owner;
    private final List<UIElement> items = new ArrayList<>();

    public UIElementCollection(Panel owner) {
        this.owner = owner;
    }

    @Override
    public UIElement get(int index) {
        return items.get(index);
    }

    @Override
    public int size() {
        return items.size();
    }

    @Override
    public boolean add(UIElement element) {
        if (element == null) {
            return items.add(null);
        }
        owner.addVisualChild(element);
        return items.add(element);
    }

    @Override
    public void add(int index, UIElement element) {
        if (element != null) {
            owner.addVisualChild(element);
        }
        items.add(index, element);
    }

    @Override
    public boolean addAll(Collection<? extends UIElement> c) {
        boolean changed = false;
        for (UIElement element : c) {
            changed |= add(element);
        }
        return changed;
    }

    @Override
    public boolean addAll(int index, Collection<? extends UIElement> c) {
        for (UIElement element : c) {
            add(index++, element);
        }
        return !c.isEmpty();
    }

    @Override
    public UIElement set(int index, UIElement element) {
        UIElement old = items.set(index, element);
        if (old != null) {
            owner.removeVisualChild(old);
        }
        if (element != null) {
            owner.addVisualChild(element);
        }
        return old;
    }

    @Override
    public UIElement remove(int index) {
        UIElement element = items.remove(index);
        if (element != null) {
            owner.removeVisualChild(element);
        }
        return element;
    }

    @Override
    public boolean remove(Object o) {
        if (!items.remove(o)) {
            return false;
        }
        if (o instanceof UIElement) {
            owner.removeVisualChild((UIElement) o);
        }
        return true;
    }

    @Override
    public void clear() {
        owner.clearVisualChildren();
        items.clear();
    }
}
