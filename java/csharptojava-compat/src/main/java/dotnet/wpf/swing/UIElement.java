package dotnet.wpf.swing;

import dotnet.wpf.swing.input.IInputElement;
import dotnet.wpf.swing.input.KeyEventArgs;
import dotnet.wpf.swing.input.MouseButtonEventArgs;
import dotnet.wpf.swing.input.MouseEventArgs;
import dotnet.wpf.swing.input.MouseWheelEventArgs;
import dotnet.wpf.swing.media.Visual;
import dotnet.wpf.swing.threading.Dispatcher;

import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

/**
 * System.Windows.UIElement compatibility base class (element-level events live here).
 * Generated code subscribes via {@code addMouseLeftButtonDownListener(...)} etc.
 */
public class UIElement extends Visual implements IInputElement {

    private Visibility visibility = Visibility.Visible;
    private boolean isHitTestVisible = true;
    private boolean isEnabled = true;
    private boolean focusable;
    private boolean isMouseCaptured;
    private boolean isMouseDirectlyOver;
    private boolean isVisible = true;
    private Point renderTransformOrigin = new Point();
    private Size desiredSize = new Size();
    private Size renderSize = new Size();
    private Object focusVisualStyle;
    private final Dispatcher dispatcher = Dispatcher.getCurrentDispatcher();

    private final List<BiConsumer<Object, MouseButtonEventArgs>> mouseLeftButtonDown = new ArrayList<>();
    private final List<BiConsumer<Object, MouseButtonEventArgs>> mouseLeftButtonUp = new ArrayList<>();
    private final List<BiConsumer<Object, MouseButtonEventArgs>> mouseRightButtonDown = new ArrayList<>();
    private final List<BiConsumer<Object, MouseButtonEventArgs>> mouseRightButtonUp = new ArrayList<>();
    private final List<BiConsumer<Object, MouseEventArgs>> mouseMove = new ArrayList<>();
    private final List<BiConsumer<Object, MouseWheelEventArgs>> mouseWheel = new ArrayList<>();
    private final List<BiConsumer<Object, MouseEventArgs>> mouseEnter = new ArrayList<>();
    private final List<BiConsumer<Object, MouseEventArgs>> mouseLeave = new ArrayList<>();
    private final List<BiConsumer<Object, KeyEventArgs>> keyDown = new ArrayList<>();
    private final List<BiConsumer<Object, KeyEventArgs>> keyUp = new ArrayList<>();

    public Visibility getVisibility() {
        return visibility;
    }

    public void setVisibility(Visibility value) {
        this.visibility = value;
        this.isVisible = value == Visibility.Visible;
    }

    public boolean getIsVisible() {
        return isVisible;
    }

    public boolean getIsHitTestVisible() {
        return isHitTestVisible;
    }

    public void setIsHitTestVisible(boolean value) {
        isHitTestVisible = value;
    }

    public boolean getIsEnabled() {
        return isEnabled;
    }

    public void setIsEnabled(boolean value) {
        isEnabled = value;
    }

    @Override
    public boolean isFocusable() {
        return focusable;
    }

    @Override
    public void setFocusable(boolean value) {
        this.focusable = value;
    }

    public boolean getFocusable() {
        return focusable;
    }

    public Object getFocusVisualStyle() {
        return focusVisualStyle;
    }

    public void setFocusVisualStyle(Object value) {
        focusVisualStyle = value;
    }

    public boolean getIsMouseCaptured() {
        return isMouseCaptured;
    }

    public boolean getIsMouseDirectlyOver() {
        return isMouseDirectlyOver;
    }

    public boolean getIsMouseOver() {
        return isMouseDirectlyOver;
    }

    public Size getDesiredSize() {
        return desiredSize;
    }

    public Size getRenderSize() {
        return renderSize;
    }

    public void setRenderSize(Size value) {
        renderSize = value;
    }

    public Point getRenderTransformOrigin() {
        return renderTransformOrigin;
    }

    public void setRenderTransformOrigin(Point value) {
        renderTransformOrigin = value;
    }

    public Dispatcher getDispatcher() {
        return dispatcher;
    }

    public UIElement getParent() {
        Visual p = getVisualParent();
        return p instanceof UIElement ? (UIElement) p : null;
    }

    // ---- layout ----

    public void measure(Size availableSize) {
        Size s = measureOverride(availableSize);
        desiredSize = s == null ? new Size() : s;
    }

    protected Size measureOverride(Size availableSize) {
        return availableSize;
    }

    public void arrange(Rect finalRect) {
        Size s = arrangeOverride(new Size(finalRect.getWidth(), finalRect.getHeight()));
        renderSize = s == null ? new Size(finalRect.getWidth(), finalRect.getHeight()) : s;
    }

    protected Size arrangeOverride(Size finalSize) {
        return finalSize;
    }

    public void updateLayout() {
        // no-op in the Swing-backed compat layer
    }

    public void invalidateVisual() {
        // no-op: Swing repaints on its own schedule
    }

    public void invalidateMeasure() {
    }

    public void invalidateArrange() {
    }

    public boolean captureMouse() {
        isMouseCaptured = true;
        return true;
    }

    public void releaseMouseCapture() {
        isMouseCaptured = false;
    }

    public boolean focus() {
        return true;
    }

    public void raiseEvent(RoutedEventArgs e) {
        if (e == null) return;
        e.setHandled(false);
    }

    // ---- event subscription (converter emits addXxxListener / removeXxxListener) ----

    public void addMouseLeftButtonDownListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseLeftButtonDown.add(handler);
    }

    public void removeMouseLeftButtonDownListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseLeftButtonDown.remove(handler);
    }

    public void addMouseLeftButtonUpListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseLeftButtonUp.add(handler);
    }

    public void removeMouseLeftButtonUpListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseLeftButtonUp.remove(handler);
    }

    public void addMouseRightButtonDownListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseRightButtonDown.add(handler);
    }

    public void removeMouseRightButtonDownListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseRightButtonDown.remove(handler);
    }

    public void addMouseRightButtonUpListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseRightButtonUp.add(handler);
    }

    public void removeMouseRightButtonUpListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseRightButtonUp.remove(handler);
    }

    public void addMouseMoveListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseMove.add(handler);
    }

    public void removeMouseMoveListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseMove.remove(handler);
    }

    public void addMouseWheelListener(BiConsumer<Object, MouseWheelEventArgs> handler) {
        mouseWheel.add(handler);
    }

    public void removeMouseWheelListener(BiConsumer<Object, MouseWheelEventArgs> handler) {
        mouseWheel.remove(handler);
    }

    public void addMouseEnterListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseEnter.add(handler);
    }

    public void removeMouseEnterListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseEnter.remove(handler);
    }

    public void addMouseLeaveListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseLeave.add(handler);
    }

    public void removeMouseLeaveListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseLeave.remove(handler);
    }

    public void addKeyDownListener(BiConsumer<Object, KeyEventArgs> handler) {
        keyDown.add(handler);
    }

    public void removeKeyDownListener(BiConsumer<Object, KeyEventArgs> handler) {
        keyDown.remove(handler);
    }

    public void addKeyUpListener(BiConsumer<Object, KeyEventArgs> handler) {
        keyUp.add(handler);
    }

    public void removeKeyUpListener(BiConsumer<Object, KeyEventArgs> handler) {
        keyUp.remove(handler);
    }

    // ---- raising (used by tests or to simulate input) ----

    public void raiseMouseLeftButtonDown(MouseButtonEventArgs e) {
        for (BiConsumer<Object, MouseButtonEventArgs> h : new ArrayList<>(mouseLeftButtonDown)) {
            h.accept(this, e);
        }
    }

    public void raiseMouseLeftButtonUp(MouseButtonEventArgs e) {
        for (BiConsumer<Object, MouseButtonEventArgs> h : new ArrayList<>(mouseLeftButtonUp)) {
            h.accept(this, e);
        }
    }

    public void raiseMouseRightButtonDown(MouseButtonEventArgs e) {
        for (BiConsumer<Object, MouseButtonEventArgs> h : new ArrayList<>(mouseRightButtonDown)) {
            h.accept(this, e);
        }
    }

    public void raiseMouseRightButtonUp(MouseButtonEventArgs e) {
        for (BiConsumer<Object, MouseButtonEventArgs> h : new ArrayList<>(mouseRightButtonUp)) {
            h.accept(this, e);
        }
    }

    public void raiseMouseMove(MouseEventArgs e) {
        for (BiConsumer<Object, MouseEventArgs> h : new ArrayList<>(mouseMove)) {
            h.accept(this, e);
        }
    }

    public void raiseMouseWheel(MouseWheelEventArgs e) {
        for (BiConsumer<Object, MouseWheelEventArgs> h : new ArrayList<>(mouseWheel)) {
            h.accept(this, e);
        }
    }

    public void raiseMouseEnter(MouseEventArgs e) {
        for (BiConsumer<Object, MouseEventArgs> h : new ArrayList<>(mouseEnter)) {
            h.accept(this, e);
        }
    }

    public void raiseMouseLeave(MouseEventArgs e) {
        for (BiConsumer<Object, MouseEventArgs> h : new ArrayList<>(mouseLeave)) {
            h.accept(this, e);
        }
    }

    public void raiseKeyDown(KeyEventArgs e) {
        for (BiConsumer<Object, KeyEventArgs> h : new ArrayList<>(keyDown)) {
            h.accept(this, e);
        }
    }

    public void raiseKeyUp(KeyEventArgs e) {
        for (BiConsumer<Object, KeyEventArgs> h : new ArrayList<>(keyUp)) {
            h.accept(this, e);
        }
    }

    // ---- IInputElement ----

    @Override
    public boolean isEnabled() {
        return isEnabled;
    }

    @Override
    public boolean isKeyboardFocused() {
        return false;
    }

    @Override
    public boolean isKeyboardFocusWithin() {
        return false;
    }

    @Override
    public boolean isMouseCaptured() {
        return isMouseCaptured;
    }

    @Override
    public boolean isMouseDirectlyOver() {
        return isMouseDirectlyOver;
    }

    @Override
    public boolean isMouseOver() {
        return isMouseDirectlyOver;
    }
}
