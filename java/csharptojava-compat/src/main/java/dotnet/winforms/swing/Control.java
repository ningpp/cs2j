package dotnet.winforms.swing;

import dotnet.drawing.swing.Graphics;
import dotnet.drawing.swing.Point;
import dotnet.drawing.swing.Rectangle;
import dotnet.drawing.swing.Size;

import java.awt.Color;
import java.util.function.BiConsumer;
import io.github.ningpp.compat.EventArgs;

/**
 * System.Windows.Forms.Control compatibility base class (Swing-backed stub).
 */
public class Control implements AutoCloseable {

    private int anchor;
    private Rectangle clientRectangle = new Rectangle(0, 0, 0, 0);
    private final ControlCollection controls = new ControlCollection(this);
    private DockStyle dock = DockStyle.None;
    private Color foreColor = Color.BLACK;
    private int height;
    private int left;
    private Point location = new Point(0, 0);
    private Padding margin = new Padding(0);
    private String name;
    private Control parent;
    private Size size = new Size(0, 0);
    private int tabIndex;
    private int width;

    private final java.util.List<java.util.function.BiConsumer<Object, EventArgs>> clickListeners = new java.util.ArrayList<>();

    private static int mouseButtons;

    /**
     * System.Windows.Forms.Control.MouseButtons compatibility: returns the
     * currently pressed mouse buttons (MouseButtons flags), or 0 (None) when
     * no button state is tracked.
     */
    public static int getMouseButtons() {
        return mouseButtons;
    }

    public void addClickListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        if (handler != null) clickListeners.add(handler);
    }

    public void removeClickListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        clickListeners.remove(handler);
    }

    protected void fireClick(Object sender, EventArgs args) {
        for (java.util.function.BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(clickListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, EventArgs>> doubleClickListeners = new java.util.ArrayList<>();

    public void addDoubleClickListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        if (handler != null) doubleClickListeners.add(handler);
    }

    public void removeDoubleClickListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        doubleClickListeners.remove(handler);
    }

    protected void fireDoubleClick(Object sender, EventArgs args) {
        for (java.util.function.BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(doubleClickListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, KeyEventArgs>> keyDownListeners = new java.util.ArrayList<>();

    public void addKeyDownListener(java.util.function.BiConsumer<Object, KeyEventArgs> handler) {
        if (handler != null) keyDownListeners.add(handler);
    }

    public void removeKeyDownListener(java.util.function.BiConsumer<Object, KeyEventArgs> handler) {
        keyDownListeners.remove(handler);
    }

    protected void fireKeyDown(Object sender, KeyEventArgs args) {
        for (java.util.function.BiConsumer<Object, KeyEventArgs> h : new java.util.ArrayList<>(keyDownListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, KeyPressEventArgs>> keyPressListeners = new java.util.ArrayList<>();

    public void addKeyPressListener(java.util.function.BiConsumer<Object, KeyPressEventArgs> handler) {
        if (handler != null) keyPressListeners.add(handler);
    }

    public void removeKeyPressListener(java.util.function.BiConsumer<Object, KeyPressEventArgs> handler) {
        keyPressListeners.remove(handler);
    }

    protected void fireKeyPress(Object sender, KeyPressEventArgs args) {
        for (java.util.function.BiConsumer<Object, KeyPressEventArgs> h : new java.util.ArrayList<>(keyPressListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, KeyEventArgs>> keyUpListeners = new java.util.ArrayList<>();

    public void addKeyUpListener(java.util.function.BiConsumer<Object, KeyEventArgs> handler) {
        if (handler != null) keyUpListeners.add(handler);
    }

    public void removeKeyUpListener(java.util.function.BiConsumer<Object, KeyEventArgs> handler) {
        keyUpListeners.remove(handler);
    }

    protected void fireKeyUp(Object sender, KeyEventArgs args) {
        for (java.util.function.BiConsumer<Object, KeyEventArgs> h : new java.util.ArrayList<>(keyUpListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, EventArgs>> mouseCaptureChangedListeners = new java.util.ArrayList<>();

    public void addMouseCaptureChangedListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        if (handler != null) mouseCaptureChangedListeners.add(handler);
    }

    public void removeMouseCaptureChangedListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        mouseCaptureChangedListeners.remove(handler);
    }

    protected void fireMouseCaptureChanged(Object sender, EventArgs args) {
        for (java.util.function.BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(mouseCaptureChangedListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, MouseEventArgs>> mouseClickListeners = new java.util.ArrayList<>();

    public void addMouseClickListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        if (handler != null) mouseClickListeners.add(handler);
    }

    public void removeMouseClickListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        mouseClickListeners.remove(handler);
    }

    protected void fireMouseClick(Object sender, MouseEventArgs args) {
        for (java.util.function.BiConsumer<Object, MouseEventArgs> h : new java.util.ArrayList<>(mouseClickListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, MouseEventArgs>> mouseDoubleClickListeners = new java.util.ArrayList<>();

    public void addMouseDoubleClickListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        if (handler != null) mouseDoubleClickListeners.add(handler);
    }

    public void removeMouseDoubleClickListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        mouseDoubleClickListeners.remove(handler);
    }

    protected void fireMouseDoubleClick(Object sender, MouseEventArgs args) {
        for (java.util.function.BiConsumer<Object, MouseEventArgs> h : new java.util.ArrayList<>(mouseDoubleClickListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, MouseEventArgs>> mouseDownListeners = new java.util.ArrayList<>();

    public void addMouseDownListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        if (handler != null) mouseDownListeners.add(handler);
    }

    public void removeMouseDownListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        mouseDownListeners.remove(handler);
    }

    protected void fireMouseDown(Object sender, MouseEventArgs args) {
        for (java.util.function.BiConsumer<Object, MouseEventArgs> h : new java.util.ArrayList<>(mouseDownListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, EventArgs>> mouseEnterListeners = new java.util.ArrayList<>();

    public void addMouseEnterListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        if (handler != null) mouseEnterListeners.add(handler);
    }

    public void removeMouseEnterListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        mouseEnterListeners.remove(handler);
    }

    protected void fireMouseEnter(Object sender, EventArgs args) {
        for (java.util.function.BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(mouseEnterListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, EventArgs>> mouseHoverListeners = new java.util.ArrayList<>();

    public void addMouseHoverListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        if (handler != null) mouseHoverListeners.add(handler);
    }

    public void removeMouseHoverListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        mouseHoverListeners.remove(handler);
    }

    protected void fireMouseHover(Object sender, EventArgs args) {
        for (java.util.function.BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(mouseHoverListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, EventArgs>> mouseLeaveListeners = new java.util.ArrayList<>();

    public void addMouseLeaveListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        if (handler != null) mouseLeaveListeners.add(handler);
    }

    public void removeMouseLeaveListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        mouseLeaveListeners.remove(handler);
    }

    protected void fireMouseLeave(Object sender, EventArgs args) {
        for (java.util.function.BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(mouseLeaveListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, MouseEventArgs>> mouseMoveListeners = new java.util.ArrayList<>();

    public void addMouseMoveListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        if (handler != null) mouseMoveListeners.add(handler);
    }

    public void removeMouseMoveListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        mouseMoveListeners.remove(handler);
    }

    protected void fireMouseMove(Object sender, MouseEventArgs args) {
        for (java.util.function.BiConsumer<Object, MouseEventArgs> h : new java.util.ArrayList<>(mouseMoveListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, MouseEventArgs>> mouseUpListeners = new java.util.ArrayList<>();

    public void addMouseUpListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        if (handler != null) mouseUpListeners.add(handler);
    }

    public void removeMouseUpListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        mouseUpListeners.remove(handler);
    }

    protected void fireMouseUp(Object sender, MouseEventArgs args) {
        for (java.util.function.BiConsumer<Object, MouseEventArgs> h : new java.util.ArrayList<>(mouseUpListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, MouseEventArgs>> mouseWheelListeners = new java.util.ArrayList<>();

    public void addMouseWheelListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        if (handler != null) mouseWheelListeners.add(handler);
    }

    public void removeMouseWheelListener(java.util.function.BiConsumer<Object, MouseEventArgs> handler) {
        mouseWheelListeners.remove(handler);
    }

    protected void fireMouseWheel(Object sender, MouseEventArgs args) {
        for (java.util.function.BiConsumer<Object, MouseEventArgs> h : new java.util.ArrayList<>(mouseWheelListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, EventArgs>> moveListeners = new java.util.ArrayList<>();

    public void addMoveListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        if (handler != null) moveListeners.add(handler);
    }

    public void removeMoveListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        moveListeners.remove(handler);
    }

    protected void fireMove(Object sender, EventArgs args) {
        for (java.util.function.BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(moveListeners)) {
            h.accept(sender, args);
        }
    }

    private final java.util.List<java.util.function.BiConsumer<Object, EventArgs>> sizeChangedListeners = new java.util.ArrayList<>();

    public void addSizeChangedListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        if (handler != null) sizeChangedListeners.add(handler);
    }

    public void removeSizeChangedListener(java.util.function.BiConsumer<Object, EventArgs> handler) {
        sizeChangedListeners.remove(handler);
    }

    protected void fireSizeChanged(Object sender, EventArgs args) {
        for (java.util.function.BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(sizeChangedListeners)) {
            h.accept(sender, args);
        }
    }


    public int getAnchor() {
        return anchor;
    }

    public void setAnchor(int value) {
        anchor = value;
    }

    public Rectangle getClientRectangle() {
        return clientRectangle;
    }

    public void setClientRectangle(Rectangle value) {
        clientRectangle = value == null ? new Rectangle(0, 0, 0, 0) : value;
    }

    
    /**
     * System.Windows.Forms.Control.SetStyle compatibility no-op.
     */
    public void setStyle(int style, boolean value) {
        // no-op: Swing peers always double-buffer; style flags are not applicable.
    }

    public ControlCollection getControls() {
        return controls;
    }

    public DockStyle getDock() {
        return dock;
    }

    public void setDock(DockStyle value) {
        dock = value;
    }

    public Color getForeColor() {
        return foreColor;
    }

    public void setForeColor(Color value) {
        foreColor = value;
    }

    public int getHeight() {
        return height;
    }

    public void setHeight(int value) {
        height = value;
    }

    public int getLeft() {
        return left;
    }

    public void setLeft(int value) {
        left = value;
    }

    public Point getLocation() {
        return location;
    }

    public void setLocation(Point value) {
        location = value == null ? new Point(0, 0) : value;
    }

    public Padding getMargin() {
        return margin;
    }

    public void setMargin(Padding value) {
        margin = value == null ? new Padding(0) : value;
    }

    public String getName() {
        return name;
    }

    public void setName(String value) {
        name = value;
    }

    public Control getParent() {
        return parent;
    }

    public void setParent(Control value) {
        parent = value;
    }

    public Size getSize() {
        return size;
    }

    public void setSize(Size value) {
        size = value == null ? new Size(0, 0) : value;
    }

    public int getTabIndex() {
        return tabIndex;
    }

    public void setTabIndex(int value) {
        tabIndex = value;
    }

    public int getWidth() {
        return width;
    }

    public void setWidth(int value) {
        width = value;
    }

    public int getBottom() {
        return location.getY() + height;
    }

    public int getTop() {
        return location.getY();
    }

    public int getRight() {
        return location.getX() + width;
    }

    public Graphics createGraphics() {
        return new Graphics(null);
    }

    public boolean focus() {
        return true;
    }

    public void invalidate() {
    }

    public void invalidate(Rectangle rc) {
    }

    protected void onKeyUp(KeyEventArgs e) {
    }

    protected void onKeyDown(KeyEventArgs e) {
    }

    protected void onMouseDown(MouseEventArgs e) {
    }

    protected void onMouseUp(MouseEventArgs e) {
    }

    protected void onMouseMove(MouseEventArgs e) {
    }

    protected void onMouseEnter(EventArgs e) {
    }

    protected void onMouseLeave(EventArgs e) {
    }

    protected void onPaint(PaintEventArgs e) {
    }

    protected void onClick(EventArgs e) {
    }

    protected void onDoubleClick(EventArgs e) {
    }

    protected void onMove(EventArgs e) {
    }

    protected void onSizeChanged(EventArgs e) {
    }

    protected void onMouseHover(EventArgs e) {
    }

    protected void onMouseCaptureChanged(EventArgs e) {
    }

    protected void onMouseClick(MouseEventArgs e) {
    }

    protected void onKeyPress(KeyPressEventArgs e) {
    }

    protected void onMouseWheel(MouseEventArgs e) {
    }

    protected void onMouseDoubleClick(MouseEventArgs e) {
    }

    public Point getMousePosition() {
        return new Point(0, 0);
    }

    public void createControl() {
    }

    public void setCursor(Cursor cursor) {
    }

    public Cursor getCursor() {
        return null;
    }

    /**
     * Mirrors System.Windows.Forms.Control.ModifierKeys (static). Named
     * getModifierKeysStatic so classes that implement an IViewer-style
     * instance getModifierKeys() can extend Control without a
     * static/instance override conflict.
     */
    public static int getModifierKeysStatic() {
        return 0;
    }

    /**
     * Mirrors System.Windows.Forms.Control.Invoke(Delegate) ? runs the action
     * immediately in the Swing-backed compat layer.
     */
    public void invoke(Runnable action) {
        if (action != null) {
            action.run();
        }
    }

    public boolean getInvokeRequired() {
        return false;
    }

    /**
     * Mirrors Control.Dispose(bool disposing).
     */
    protected void close(boolean disposing) {
        close();
    }


    public void performLayout() {
    }

    public Point pointToClient(Point p) {
        return p;
    }

    public void resumeLayout() {
    }

    public void resumeLayout(boolean performLayout) {
    }

    public void sendToBack() {
    }

    public void suspendLayout() {
    }

    @Override
    public void close() {
        // no-op in the Swing-backed compat layer
    }

    /**
     * System.Windows.Forms.Control.ControlCollection compatibility class.
     */
    public static class ControlCollection implements Iterable<Control> {

        private final Control owner;
        private final java.util.List<Control> items = new java.util.ArrayList<>();

        public ControlCollection(Control owner) {
            this.owner = owner;
        }

        public Control getOwner() {
            return owner;
        }

        public void add(Control control) {
            if (control != null && !items.contains(control)) {
                items.add(control);
                control.setParent(owner);
            }
        }

        public void addRange(Control[] controls) {
            if (controls == null) return;
            for (Control control : controls) {
                add(control);
            }
        }

        public boolean contains(Control control) {
            return items.contains(control);
        }

        public void remove(Control control) {
            items.remove(control);
            if (control != null && control.getParent() == owner) {
                control.setParent(null);
            }
        }

        public void setChildIndex(Control control, int index) {
            if (control == null || !items.remove(control)) return;
            int bounded = Math.max(0, Math.min(index, items.size()));
            items.add(bounded, control);
        }

        public int getCount() {
            return items.size();
        }

        @Override
        public java.util.Iterator<Control> iterator() {
            return items.iterator();
        }

        public Control get(int index) {
            return items.get(index);
        }
    }
}
