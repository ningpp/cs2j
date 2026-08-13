package dotnet.wpf.swing;

import dotnet.wpf.swing.input.CommandBindingCollection;
import dotnet.wpf.swing.input.IInputElement;
import dotnet.wpf.swing.input.InputBindingCollection;
import dotnet.wpf.swing.input.KeyEventArgs;
import dotnet.wpf.swing.input.MouseButtonEventArgs;
import dotnet.wpf.swing.input.MouseEventArgs;
import dotnet.wpf.swing.input.MouseWheelEventArgs;
import dotnet.wpf.swing.media.DrawingContext;
import dotnet.wpf.swing.media.Visual;
import dotnet.wpf.swing.threading.Dispatcher;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.function.BiConsumer;

/**
 * System.Windows.UIElement compatibility base class (element-level events live here).
 * Generated code subscribes via {@code addMouseLeftButtonDownListener(...)} etc.
 */
public class UIElement extends Visual implements IInputElement {

    public static final RoutedEvent GotKeyboardFocusEvent = new RoutedEvent("GotKeyboardFocus");
    public static final RoutedEvent LostKeyboardFocusEvent = new RoutedEvent("LostKeyboardFocus");
    public static final RoutedEvent PreviewMouseLeftButtonDownEvent = new RoutedEvent("PreviewMouseLeftButtonDown");

    private Visibility visibility = Visibility.Visible;
    private boolean isHitTestVisible = true;
    private boolean isEnabled = true;
    private boolean focusable;
    private boolean isKeyboardFocusWithin;
    private boolean isKeyboardFocused;
    private boolean isMouseCaptured;
    private boolean isMouseDirectlyOver;
    private boolean isVisible = true;
    private Point renderTransformOrigin = new Point();
    private Size desiredSize = new Size();
    private Size renderSize = new Size();
    private Object focusVisualStyle;
    private final Dispatcher dispatcher = Dispatcher.getCurrentDispatcher();
    private CommandBindingCollection commandBindings;
    private InputBindingCollection inputBindings;

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
    private final List<BiConsumer<Object, RoutedEventArgs>> gotFocusListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> lostFocusListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> gotMouseCaptureListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> lostMouseCaptureListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> layoutUpdatedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> isVisibleChangedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> isEnabledChangedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> focusableChangedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> isHitTestVisibleChangedListeners = new ArrayList<>();
    private final Map<RoutedEvent, List<Object>> routedHandlers = new HashMap<>();

    public Visibility getVisibility() {
        return visibility;
    }

    public void setVisibility(Visibility value) {
        Visibility old = this.visibility;
        if (old == value) {
            return;
        }
        this.visibility = value;
        this.isVisible = value == Visibility.Visible;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(isVisibleChangedListeners)) {
            h.accept(this, e);
        }
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public boolean getIsVisible() {
        return isVisible;
    }

    public boolean getIsHitTestVisible() {
        return isHitTestVisible;
    }

    public void setIsHitTestVisible(boolean value) {
        if (isHitTestVisible == value) {
            return;
        }
        isHitTestVisible = value;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(isHitTestVisibleChangedListeners)) {
            h.accept(this, e);
        }
    }

    public boolean getIsEnabled() {
        return isEnabled;
    }

    public void setIsEnabled(boolean value) {
        if (isEnabled == value) {
            return;
        }
        isEnabled = value;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(isEnabledChangedListeners)) {
            h.accept(this, e);
        }
    }

    @Override
    public boolean isFocusable() {
        return focusable;
    }

    @Override
    public void setFocusable(boolean value) {
        if (focusable == value) {
            return;
        }
        this.focusable = value;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(focusableChangedListeners)) {
            h.accept(this, e);
        }
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
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public void invalidateMeasure() {
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public void invalidateArrange() {
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public boolean captureMouse() {
        if (isMouseCaptured) {
            return true;
        }
        isMouseCaptured = true;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(gotMouseCaptureListeners)) {
            h.accept(this, e);
        }
        return true;
    }

    public void releaseMouseCapture() {
        if (!isMouseCaptured) {
            return;
        }
        isMouseCaptured = false;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(lostMouseCaptureListeners)) {
            h.accept(this, e);
        }
    }

    public boolean focus() {
        dotnet.wpf.swing.internal.ScenePanel.requestFocusOn(this);
        return true;
    }

    public boolean getIsKeyboardFocusWithin() {
        return isKeyboardFocusWithin;
    }

    /** True when this element holds keyboard focus (System.Windows.UIElement.IsKeyboardFocused). */
    public boolean getIsKeyboardFocused() {
        return isKeyboardFocused;
    }

    /** True when this element holds keyboard focus (alias of IsKeyboardFocused). */
    public boolean getIsFocused() {
        return isKeyboardFocused;
    }

    /** Internal: updated by the ScenePanel / Keyboard focus routing. */
    public void setIsKeyboardFocused(boolean value) {
        isKeyboardFocused = value;
    }

    @Override
    public boolean isKeyboardFocusWithin() {
        return isKeyboardFocusWithin;
    }

    public void raiseEvent(RoutedEventArgs e) {
        if (e == null) {
            return;
        }
        if (e.getSource() == null) {
            e.setSource(this);
        }
        if (e.getRoutedEvent() instanceof RoutedEvent) {
            invokeRoutedHandlers((RoutedEvent) e.getRoutedEvent(), e);
        }
        if (!e.getHandled()) {
            UIElement parent = getParent();
            if (parent != null) {
                parent.raiseEvent(e);
            }
        }
    }

    private void invokeRoutedHandlers(RoutedEvent routedEvent, RoutedEventArgs e) {
        List<Object> handlers = routedHandlers.get(routedEvent);
        if (handlers == null) {
            return;
        }
        for (Object handler : new ArrayList<>(handlers)) {
            if (handler instanceof BiConsumer) {
                //noinspection unchecked
                ((BiConsumer<Object, RoutedEventArgs>) handler).accept(this, e);
            } else if (handler instanceof dotnet.wpf.swing.RoutedEventHandler) {
                ((dotnet.wpf.swing.RoutedEventHandler) handler).accept(this, e);
            }
            if (e.getHandled()) {
                return;
            }
        }
    }

    // ---- routed event registration (AddHandler/RemoveHandler) ----

    public void addHandler(RoutedEvent routedEvent, Object handler) {
        if (routedEvent == null || handler == null) {
            return;
        }
        routedHandlers.computeIfAbsent(routedEvent, k -> new ArrayList<>()).add(handler);
    }

    public void removeHandler(RoutedEvent routedEvent, Object handler) {
        List<Object> handlers = routedHandlers.get(routedEvent);
        if (handlers != null) {
            handlers.remove(handler);
        }
    }

    // ---- render hook (System.Windows.UIElement.OnRender; public in the compat layer) ----

    /** Custom-draw hook called by the renderer each paint pass. */
    public void onRender(DrawingContext drawingContext) {
        // overridable hook
    }

    // ---- layout lifecycle ----

    public void raiseLayoutUpdated() {
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(layoutUpdatedListeners)) {
            h.accept(this, new RoutedEventArgs());
        }
    }

    public void addLayoutUpdatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        layoutUpdatedListeners.add(handler);
    }

    public void removeLayoutUpdatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        layoutUpdatedListeners.remove(handler);
    }

    // ---- changed-event subscription ----

    public void addIsVisibleChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        isVisibleChangedListeners.add(handler);
    }

    public void removeIsVisibleChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        isVisibleChangedListeners.remove(handler);
    }

    public void addIsEnabledChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        isEnabledChangedListeners.add(handler);
    }

    public void removeIsEnabledChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        isEnabledChangedListeners.remove(handler);
    }

    public void addFocusableChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        focusableChangedListeners.add(handler);
    }

    public void removeFocusableChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        focusableChangedListeners.remove(handler);
    }

    public void addIsHitTestVisibleChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        isHitTestVisibleChangedListeners.add(handler);
    }

    public void removeIsHitTestVisibleChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        isHitTestVisibleChangedListeners.remove(handler);
    }

    // ---- focus-event subscription ----

    public void addGotFocusListener(BiConsumer<Object, RoutedEventArgs> handler) {
        gotFocusListeners.add(handler);
    }

    public void removeGotFocusListener(BiConsumer<Object, RoutedEventArgs> handler) {
        gotFocusListeners.remove(handler);
    }

    public void addLostFocusListener(BiConsumer<Object, RoutedEventArgs> handler) {
        lostFocusListeners.add(handler);
    }

    public void removeLostFocusListener(BiConsumer<Object, RoutedEventArgs> handler) {
        lostFocusListeners.remove(handler);
    }

    /** Internal: raised by the focus routing when this element gains keyboard focus. */
    public void raiseGotFocus() {
        isKeyboardFocused = true;
        isKeyboardFocusWithin = true;
        RoutedEventArgs e = new RoutedEventArgs();
        e.setSource(this);
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(gotFocusListeners)) {
            h.accept(this, e);
        }
        raiseEvent(GotKeyboardFocusEvent, this);
    }

    /** Internal: raised by the focus routing when this element loses keyboard focus. */
    public void raiseLostFocus() {
        isKeyboardFocused = false;
        RoutedEventArgs e = new RoutedEventArgs();
        e.setSource(this);
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(lostFocusListeners)) {
            h.accept(this, e);
        }
        raiseEvent(LostKeyboardFocusEvent, this);
    }

    /** Raises a bubbling routed event through this element's handler table. */
    public void raiseEvent(RoutedEvent routedEvent, Object source) {
        RoutedEventArgs e = new RoutedEventArgs();
        e.setRoutedEvent(routedEvent);
        e.setSource(source == null ? this : source);
        raiseEvent(e);
    }

    // ---- capture-event subscription ----

    public void addGotMouseCaptureListener(BiConsumer<Object, RoutedEventArgs> handler) {
        gotMouseCaptureListeners.add(handler);
    }

    public void removeGotMouseCaptureListener(BiConsumer<Object, RoutedEventArgs> handler) {
        gotMouseCaptureListeners.remove(handler);
    }

    public void addLostMouseCaptureListener(BiConsumer<Object, RoutedEventArgs> handler) {
        lostMouseCaptureListeners.add(handler);
    }

    public void removeLostMouseCaptureListener(BiConsumer<Object, RoutedEventArgs> handler) {
        lostMouseCaptureListeners.remove(handler);
    }

    // ---- command/input bindings ----

    public CommandBindingCollection getCommandBindings() {
        if (commandBindings == null) {
            commandBindings = new CommandBindingCollection();
        }
        return commandBindings;
    }

    public InputBindingCollection getInputBindings() {
        if (inputBindings == null) {
            inputBindings = new InputBindingCollection();
        }
        return inputBindings;
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

    // ---- IInputElement-qualified aliases ----
    // The converter prefixes listener names with the interface name when an event
    // implements an interface event (e.g. UIElement.MouseEnter implements
    // IInputElement.MouseEnter -> addIInputElementMouseEnterListener).

    public void addIInputElementKeyDownListener(dotnet.wpf.swing.input.KeyEventHandler handler) {
        if (handler != null) keyDown.add(handler::accept);
    }

    public void removeIInputElementKeyDownListener(dotnet.wpf.swing.input.KeyEventHandler handler) {
        if (handler != null) {
            java.util.function.BiConsumer<Object, KeyEventArgs> consumer = handler::accept;
            keyDown.remove(consumer);
        }
    }

    public void addIInputElementMouseEnterListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseEnter.add(handler);
    }

    public void removeIInputElementMouseEnterListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseEnter.remove(handler);
    }

    public void addIInputElementMouseLeaveListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseLeave.add(handler);
    }

    public void removeIInputElementMouseLeaveListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseLeave.remove(handler);
    }

    public void addIInputElementMouseLeftButtonDownListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseLeftButtonDown.add(handler);
    }

    public void removeIInputElementMouseLeftButtonDownListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseLeftButtonDown.remove(handler);
    }

    public void addIInputElementMouseLeftButtonUpListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseLeftButtonUp.add(handler);
    }

    public void removeIInputElementMouseLeftButtonUpListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseLeftButtonUp.remove(handler);
    }

    public void addIInputElementMouseRightButtonDownListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseRightButtonDown.add(handler);
    }

    public void removeIInputElementMouseRightButtonDownListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseRightButtonDown.remove(handler);
    }

    public void addIInputElementMouseRightButtonUpListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseRightButtonUp.add(handler);
    }

    public void removeIInputElementMouseRightButtonUpListener(BiConsumer<Object, MouseButtonEventArgs> handler) {
        mouseRightButtonUp.remove(handler);
    }

    public void addIInputElementMouseMoveListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseMove.add(handler);
    }

    public void removeIInputElementMouseMoveListener(BiConsumer<Object, MouseEventArgs> handler) {
        mouseMove.remove(handler);
    }

    public void addIInputElementMouseWheelListener(BiConsumer<Object, MouseWheelEventArgs> handler) {
        mouseWheel.add(handler);
    }

    public void removeIInputElementMouseWheelListener(BiConsumer<Object, MouseWheelEventArgs> handler) {
        mouseWheel.remove(handler);
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
        return isKeyboardFocusWithin;
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
