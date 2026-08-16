package dotnet.wpf.swing.internal;

import dotnet.wpf.swing.ContentControl;
import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;
import dotnet.wpf.swing.RoutedEventArgs;
import dotnet.wpf.swing.Size;
import dotnet.wpf.swing.Thickness;
import dotnet.wpf.swing.UIElement;
import dotnet.wpf.swing.Visibility;
import dotnet.wpf.swing.Window;
import dotnet.wpf.swing.controls.Border;
import dotnet.wpf.swing.controls.Canvas;
import dotnet.wpf.swing.controls.ContextMenu;
import dotnet.wpf.swing.controls.ContextMenuService;
import dotnet.wpf.swing.controls.DockPanel;
import dotnet.wpf.swing.controls.Image;
import dotnet.wpf.swing.controls.Menu;
import dotnet.wpf.swing.controls.MenuItem;
import dotnet.wpf.swing.controls.Panel;
import dotnet.wpf.swing.controls.StatusBar;
import dotnet.wpf.swing.controls.TextBlock;
import dotnet.wpf.swing.controls.TextBox;
import dotnet.wpf.swing.controls.TextBoxBase;
import dotnet.wpf.swing.controls.ToolBar;
import dotnet.wpf.swing.controls.Viewbox;
import dotnet.wpf.swing.input.CommandBinding;
import dotnet.wpf.swing.input.ExecutedRoutedEventArgs;
import dotnet.wpf.swing.input.InputBinding;
import dotnet.wpf.swing.input.Key;
import dotnet.wpf.swing.input.KeyEventArgs;
import dotnet.wpf.swing.input.Keyboard;
import dotnet.wpf.swing.input.ModifierKeys;
import dotnet.wpf.swing.input.MouseButton;
import dotnet.wpf.swing.input.MouseButtonEventArgs;
import dotnet.wpf.swing.input.MouseButtonState;
import dotnet.wpf.swing.input.MouseEventArgs;
import dotnet.wpf.swing.input.MouseWheelEventArgs;
import dotnet.wpf.swing.media.ArcSegment;
import dotnet.wpf.swing.media.BezierSegment;
import dotnet.wpf.swing.media.Brush;
import dotnet.wpf.swing.media.BitmapSource;
import dotnet.wpf.swing.media.DrawingContext;
import dotnet.wpf.swing.media.EllipseGeometry;
import dotnet.wpf.swing.media.FillRule;
import dotnet.wpf.swing.FontStyle;
import dotnet.wpf.swing.FontWeight;
import dotnet.wpf.swing.media.Geometry;
import dotnet.wpf.swing.media.ImageSource;
import dotnet.wpf.swing.media.LineSegment;
import dotnet.wpf.swing.media.Matrix;
import dotnet.wpf.swing.media.MatrixTransform;
import dotnet.wpf.swing.media.PathFigure;
import dotnet.wpf.swing.media.PathGeometry;
import dotnet.wpf.swing.media.PathSegment;
import dotnet.wpf.swing.media.PolyBezierSegment;
import dotnet.wpf.swing.media.PolyLineSegment;
import dotnet.wpf.swing.media.PolyQuadraticBezierSegment;
import dotnet.wpf.swing.media.QuadraticBezierSegment;
import dotnet.wpf.swing.media.RectangleGeometry;
import dotnet.wpf.swing.media.StreamGeometry;
import dotnet.wpf.swing.media.Stretch;
import dotnet.wpf.swing.shapes.Shape;

import javax.swing.JComponent;
import javax.swing.JMenu;
import javax.swing.JMenuItem;
import javax.swing.JPopupMenu;
import javax.swing.event.PopupMenuEvent;
import javax.swing.event.PopupMenuListener;
import java.awt.BasicStroke;
import java.awt.Color;
import java.awt.Font;
import java.awt.FontMetrics;
import java.awt.Graphics;
import java.awt.Graphics2D;
import java.awt.RenderingHints;
import java.awt.event.KeyEvent;
import java.awt.event.MouseEvent;
import java.awt.event.MouseWheelEvent;
import java.awt.geom.AffineTransform;
import java.awt.geom.Ellipse2D;
import java.awt.geom.Path2D;
import java.awt.geom.Rectangle2D;
import java.awt.geom.RoundRectangle2D;
import java.awt.image.BufferedImage;
import java.util.ArrayList;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.WeakHashMap;

/**
 * Swing-backed renderer for the WPF compat layer.
 *
 * <p>Paints a {@link UIElement} scene graph with Java2D, lays out panels
 * (Canvas / DockPanel / Border / Viewbox / plain panels), and routes real
 * Swing mouse/keyboard input to the WPF-style listener lists on the hit
 * elements. It also updates {@code ActualWidth}/{@code ActualHeight} and
 * raises the {@code Loaded} routed event once per window show.</p>
 */
public final class ScenePanel extends JComponent {

    private static final double INF = Double.POSITIVE_INFINITY;
    private static final Color DEFAULT_FOREGROUND = Color.BLACK;
    private static final Map<UIElement, ScenePanel> HOSTS =
            Collections.synchronizedMap(new WeakHashMap<>());

    private final Window owner;
    private final IdentityHashMap<UIElement, Rect> bounds = new IdentityHashMap<>();
    private final List<UIElement> hitOrder = new ArrayList<>();
    private final IdentityHashMap<FrameworkElement, Boolean> loadedRaised = new IdentityHashMap<>();

    private UIElement root;
    private UIElement focusedElement;
    private UIElement hoverElement;
    private boolean firstPaint = true;

    public ScenePanel(Window owner) {
        this.owner = owner;
        setFocusable(true);
        addMouseListener(new java.awt.event.MouseAdapter() {
            @Override
            public void mousePressed(MouseEvent e) {
                onMousePressed(e);
            }

            @Override
            public void mouseReleased(MouseEvent e) {
                onMouseReleased(e);
            }

            @Override
            public void mouseEntered(MouseEvent e) {
                onMouseMoved(e);
            }
        });
        addMouseMotionListener(new java.awt.event.MouseMotionAdapter() {
            @Override
            public void mouseMoved(MouseEvent e) {
                onMouseMoved(e);
            }

            @Override
            public void mouseDragged(MouseEvent e) {
                onMouseMoved(e);
            }
        });
        addMouseWheelListener(this::onMouseWheel);
        addKeyListener(new java.awt.event.KeyAdapter() {
            @Override
            public void keyPressed(KeyEvent e) {
                onKeyPressed(e);
            }

            @Override
            public void keyReleased(KeyEvent e) {
                onKeyReleased(e);
            }

            @Override
            public void keyTyped(KeyEvent e) {
                onKeyTyped(e);
            }
        });
    }

    public void setRoot(UIElement rootElement) {
        this.root = rootElement;
        loadedRaised.clear();
    }

    public UIElement getRoot() {
        return root;
    }

    // ------------------------------------------------------------------
    // static helpers used by the element model (focus, refresh)
    // ------------------------------------------------------------------

    /** Schedules a repaint of the window hosting the element (no-op when detached). */
    public static void refresh(UIElement element) {
        ScenePanel host = hostOf(element);
        if (host != null) {
            host.repaintScene();
        }
    }

    /** Requests keyboard focus on the given element inside its host window. */
    public static boolean requestFocusOn(UIElement element) {
        if (element == null) {
            return false;
        }
        ScenePanel host = hostOf(element);
        if (host != null) {
            host.setFocusedElement(element);
            return true;
        }
        Keyboard.focus(element);
        return true;
    }

    private static ScenePanel hostOf(UIElement element) {
        return element == null ? null : HOSTS.get(element);
    }

    private void repaintScene() {
        if (isDisplayable()) {
            UiLoop.get().invokeLater(this::repaint);
        }
    }

    // ------------------------------------------------------------------
    // focus
    // ------------------------------------------------------------------

    public void setFocusedElement(UIElement element) {
        if (element == this.focusedElement) {
            if (element != null && isDisplayable()) {
                requestFocusInWindow();
            }
            repaint();
            return;
        }
        this.focusedElement = element;
        Keyboard.focus(element);
        if (isDisplayable()) {
            requestFocusInWindow();
        }
        repaint();
    }

    public UIElement getFocusedElement() {
        return focusedElement;
    }

    /**
     * Unloads the element tree: raises {@code Unloaded} once per element,
     * clears the loaded bookkeeping (a later show re-raises {@code Loaded})
     * and drops focus/hover state.
     */
    public void unloadTree() {
        if (root != null) {
            unloadRecursive(root);
        }
        loadedRaised.clear();
        if (focusedElement != null) {
            Keyboard.clearFocus();
            focusedElement = null;
        }
        hoverElement = null;
    }

    private void unloadRecursive(UIElement e) {
        if (e == null) {
            return;
        }
        if (e instanceof FrameworkElement) {
            ((FrameworkElement) e).raiseUnloaded();
        }
        for (UIElement child : visualChildrenOf(e)) {
            unloadRecursive(child);
        }
    }

    // ------------------------------------------------------------------
    // painting: layout + draw in one pass
    // ------------------------------------------------------------------

    @Override
    protected void paintComponent(Graphics g) {
        super.paintComponent(g);
        Graphics2D g2 = (Graphics2D) g;
        g2.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
        g2.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING, RenderingHints.VALUE_TEXT_ANTIALIAS_ON);
        bounds.clear();
        hitOrder.clear();
        double w = Math.max(0, getWidth());
        double h = Math.max(0, getHeight());
        if (root != null && root.getIsVisible()) {
            layoutElement(root, 0, 0, w, h);
            raiseLayoutUpdated();
            drawElement(root, 0, 0, w, h, g2);
        }
        raiseLoadedOnce();
        if (firstPaint) {
            firstPaint = false;
            if (owner != null) {
                owner.raiseContentRendered();
            }
        }
    }

    private void raiseLayoutUpdated() {
        for (UIElement e : new ArrayList<>(hitOrder)) {
            e.raiseLayoutUpdated();
        }
    }

    private void raiseLoadedOnce() {
        if (root == null) {
            return;
        }
        raiseLoadedRecursive(root);
    }

    private void raiseLoadedRecursive(UIElement e) {
        if (e == null || !e.getIsVisible()) {
            return;
        }
        if (e instanceof FrameworkElement) {
            FrameworkElement fe = (FrameworkElement) e;
            if (loadedRaised.put(fe, Boolean.TRUE) == null) {
                fe.raiseLoaded();
            }
        }
        for (UIElement child : visualChildrenOf(e)) {
            raiseLoadedRecursive(child);
        }
    }

    /**
     * Raises {@code Loaded} on a tree without host bookkeeping (used by the
     * headless window lifecycle). Elements already loaded are skipped.
     */
    public static void raiseLoadedTree(UIElement root) {
        if (root == null || !root.getIsVisible()) {
            return;
        }
        if (root instanceof FrameworkElement) {
            FrameworkElement fe = (FrameworkElement) root;
            if (!fe.getIsLoaded()) {
                fe.raiseLoaded();
            }
        }
        for (UIElement child : visualChildrenOf(root)) {
            raiseLoadedTree(child);
        }
    }

    /**
     * Raises {@code Unloaded} on a tree without host bookkeeping (used by the
     * headless window lifecycle).
     */
    public static void raiseUnloadedTree(UIElement root) {
        if (root == null) {
            return;
        }
        if (root instanceof FrameworkElement) {
            ((FrameworkElement) root).raiseUnloaded();
        }
        for (UIElement child : visualChildrenOf(root)) {
            raiseUnloadedTree(child);
        }
    }

    /** Finds a named FrameworkElement in the visual tree rooted at {@code root}. */
    public static UIElement findByName(UIElement root, String name) {
        if (root == null || name == null || name.isEmpty()) {
            return null;
        }
        for (UIElement child : visualChildrenOf(root)) {
            if (child instanceof FrameworkElement) {
                FrameworkElement fe = (FrameworkElement) child;
                if (name.equals(fe.getName())) {
                    return fe;
                }
            }
            UIElement found = findByName(child, name);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    private static List<UIElement> visualChildrenOf(UIElement e) {
        List<UIElement> list = new ArrayList<>();
        if (e instanceof Panel) {
            list.addAll(((Panel) e).getChildren());
        } else if (e instanceof Border) {
            if (((Border) e).getChild() != null) {
                list.add(((Border) e).getChild());
            }
        } else if (e instanceof Viewbox) {
            if (((Viewbox) e).getChild() != null) {
                list.add(((Viewbox) e).getChild());
            }
        } else if (e instanceof ContentControl) {
            Object content = ((ContentControl) e).getContent();
            if (content instanceof UIElement) {
                list.add((UIElement) content);
            }
        }
        return list;
    }

    // ------------------------------------------------------------------
    // layout
    // ------------------------------------------------------------------

    private void layoutElement(UIElement e, double x, double y, double w, double h) {
        if (e == null || !e.getIsVisible()) {
            return;
        }
        HOSTS.put(e, this);
        e.measure(new Size(w, h));
        e.arrange(new Rect(x, y, w, h));
        if (e instanceof FrameworkElement) {
            FrameworkElement fe = (FrameworkElement) e;
            fe.setActualWidth(w);
            fe.setActualHeight(h);
        }
        bounds.put(e, new Rect(x, y, w, h));
        hitOrder.add(e);

        if (e instanceof Canvas) {
            for (UIElement c : ((Canvas) e).getChildren()) {
                if (c == null || !c.getIsVisible()) {
                    continue;
                }
                Size s = measureChild(c, INF, INF);
                double cx = Double.isFinite(Canvas.getLeft(c)) ? Canvas.getLeft(c) : 0;
                double cy = Double.isFinite(Canvas.getTop(c)) ? Canvas.getTop(c) : 0;
                layoutElement(c, x + cx, y + cy, s.getWidth(), s.getHeight());
            }
        } else if (e instanceof DockPanel) {
            layoutDock((DockPanel) e, x, y, w, h);
        } else if (e instanceof ToolBar || e instanceof StatusBar || e instanceof Menu) {
            layoutHorizontal((Panel) e, x, y, w, h);
        } else if (e instanceof Border) {
            Border b = (Border) e;
            Thickness pad = b.getPadding() == null ? new Thickness(0) : b.getPadding();
            Thickness bt = b.getBorderThickness() == null ? new Thickness(0) : b.getBorderThickness();
            double insetL = pad.getLeft() + bt.getLeft();
            double insetT = pad.getTop() + bt.getTop();
            double insetR = pad.getRight() + bt.getRight();
            double insetB = pad.getBottom() + bt.getBottom();
            UIElement c = b.getChild();
            if (c != null && c.getIsVisible()) {
                double availW = Math.max(0, w - insetL - insetR);
                double availH = Math.max(0, h - insetT - insetB);
                Size s = measureChild(c, availW, availH);
                layoutElement(c, x + insetL, y + insetT,
                        Math.min(s.getWidth(), availW), Math.min(s.getHeight(), availH));
            }
        } else if (e instanceof Viewbox) {
            layoutViewbox((Viewbox) e, x, y, w, h);
        } else if (e instanceof Panel) {
            for (UIElement c : ((Panel) e).getChildren()) {
                if (c != null && c.getIsVisible()) {
                    layoutElement(c, x, y, w, h);
                }
            }
        } else if (e instanceof ContentControl) {
            Object content = ((ContentControl) e).getContent();
            if (content instanceof UIElement && ((UIElement) content).getIsVisible()) {
                layoutElement((UIElement) content, x, y, w, h);
            }
        }
    }

    private void layoutDock(DockPanel panel, double x, double y, double w, double h) {
        List<UIElement> children = panel.getChildren();
        double remainingW = w;
        double remainingH = h;
        for (int i = 0; i < children.size(); i++) {
            UIElement c = children.get(i);
            if (c == null || !c.getIsVisible()) {
                continue;
            }
            boolean last = i == children.size() - 1;
            String dock = dockName(DockPanel.getDock(c));
            if (last) {
                layoutElement(c, x, y, Math.max(0, remainingW), Math.max(0, remainingH));
            } else if ("Left".equals(dock)) {
                Size s = measureChild(c, remainingW, remainingH);
                double cw = Math.min(s.getWidth(), Math.max(0, remainingW));
                layoutElement(c, x, y, cw, Math.max(0, remainingH));
                x += cw;
                remainingW -= cw;
            } else if ("Right".equals(dock)) {
                Size s = measureChild(c, remainingW, remainingH);
                double cw = Math.min(s.getWidth(), Math.max(0, remainingW));
                layoutElement(c, x + remainingW - cw, y, cw, Math.max(0, remainingH));
                remainingW -= cw;
            } else if ("Bottom".equals(dock)) {
                Size s = measureChild(c, remainingW, remainingH);
                double ch = Math.min(s.getHeight(), Math.max(0, remainingH));
                layoutElement(c, x, y + remainingH - ch, Math.max(0, remainingW), ch);
                remainingH -= ch;
            } else { // Top (default)
                Size s = measureChild(c, remainingW, remainingH);
                double ch = Math.min(s.getHeight(), Math.max(0, remainingH));
                layoutElement(c, x, y, Math.max(0, remainingW), ch);
                y += ch;
                remainingH -= ch;
            }
        }
    }

    private void layoutHorizontal(Panel bar, double x, double y, double w, double h) {
        double cursor = x;
        double remaining = w;
        for (UIElement child : bar.getChildren()) {
            if (child == null || !child.getIsVisible()) {
                continue;
            }
            Size s = measureChild(child, remaining, h);
            double cw = Math.min(s.getWidth(), Math.max(0, remaining));
            layoutElement(child, cursor, y, cw, Math.max(0, h));
            cursor += cw;
            remaining -= cw;
        }
    }

    private void layoutViewbox(Viewbox vb, double x, double y, double w, double h) {
        UIElement c = vb.getChild();
        if (c == null || !c.getIsVisible()) {
            return;
        }
        Size natural = measureChild(c, INF, INF);
        double cw = natural.getWidth();
        double ch = natural.getHeight();
        if (cw > 0 && ch > 0 && w > 0 && h > 0) {
            Stretch stretch = vb.getStretch() == null ? Stretch.Uniform : vb.getStretch();
            double sx = w / cw;
            double sy = h / ch;
            double s;
            if (stretch == Stretch.Fill) {
                cw = w;
                ch = h;
            } else if (stretch == Stretch.UniformToFill) {
                s = Math.max(sx, sy);
                cw *= s;
                ch *= s;
            } else if (stretch == Stretch.Uniform) {
                s = Math.min(sx, sy);
                cw *= s;
                ch *= s;
            }
        }
        layoutElement(c, x + (w - cw) / 2, y + (h - ch) / 2, cw, ch);
    }

    private static Size measureChild(UIElement c, double availW, double availH) {
        c.measure(new Size(availW, availH));
        Size d = c.getDesiredSize();
        double w = d != null && Double.isFinite(d.getWidth()) && d.getWidth() > 0 ? d.getWidth() : 0;
        double h = d != null && Double.isFinite(d.getHeight()) && d.getHeight() > 0 ? d.getHeight() : 0;
        if (c instanceof FrameworkElement) {
            FrameworkElement fe = (FrameworkElement) c;
            if (!Double.isNaN(fe.getWidth())) {
                w = fe.getWidth();
            }
            if (!Double.isNaN(fe.getHeight())) {
                h = fe.getHeight();
            }
        }
        return new Size(w, h);
    }

    private static String dockName(Object dock) {
        if (dock == null) {
            return "Top";
        }
        if (dock instanceof Enum<?>) {
            return ((Enum<?>) dock).name();
        }
        return String.valueOf(dock);
    }

    // ------------------------------------------------------------------
    // drawing
    // ------------------------------------------------------------------

    private void drawElement(UIElement e, double x, double y, double w, double h, Graphics2D parentG) {
        if (e == null || !e.getIsVisible()) {
            return;
        }
        Graphics2D g = (Graphics2D) parentG.create();
        try {
            g.translate(x, y);
            drawSelf(e, w, h, g);
            for (UIElement child : visualChildrenOf(e)) {
                Rect r = child == null ? null : bounds.get(child);
                if (r == null || !child.getIsVisible()) {
                    continue;
                }
                drawElement(child, r.getX() - x, r.getY() - y, r.getWidth(), r.getHeight(), g);
            }
        } finally {
            g.dispose();
        }
    }

    private void drawSelf(UIElement e, double w, double h, Graphics2D g) {
        if (e instanceof Panel) {
            Brush bg = ((Panel) e).getBackground();
            if (bg != null) {
                g.setColor(bg.toAwtColor());
                g.fillRect(0, 0, (int) Math.ceil(w), (int) Math.ceil(h));
            }
        } else if (e instanceof Border) {
            drawBorder((Border) e, w, h, g);
        } else if (e instanceof TextBlock) {
            drawTextBlock((TextBlock) e, w, h, g);
        } else if (e instanceof TextBoxBase) {
            drawTextBox((TextBoxBase) e, w, h, g);
        } else if (e instanceof MenuItem) {
            drawMenuItem((MenuItem) e, w, h, g);
        } else if (e instanceof Image) {
            drawImage((Image) e, w, h, g);
        } else if (e instanceof Shape) {
            drawShape((Shape) e, g);
        }
        replayOnRender(e, g);
    }

    /** Replays the element's {@code OnRender} drawing operations onto the canvas. */
    private static void replayOnRender(UIElement e, Graphics2D g) {
        DrawingContext ctx = new DrawingContext();
        try {
            e.onRender(ctx);
        } catch (Throwable ignored) {
            // custom render code must not crash the paint pass
        }
        for (Object op : ctx.getOperations()) {
            if (op instanceof DrawingContext.RectOp) {
                DrawingContext.RectOp r = (DrawingContext.RectOp) op;
                if (r.brush != null) {
                    g.setColor(r.brush.toAwtColor());
                    g.fillRect((int) Math.rint(r.rect.getX()), (int) Math.rint(r.rect.getY()),
                            (int) Math.ceil(r.rect.getWidth()), (int) Math.ceil(r.rect.getHeight()));
                }
            } else if (op instanceof DrawingContext.LineOp) {
                DrawingContext.LineOp l = (DrawingContext.LineOp) op;
                g.setColor(DEFAULT_FOREGROUND);
                g.drawLine((int) Math.rint(l.point0.getX()), (int) Math.rint(l.point0.getY()),
                        (int) Math.rint(l.point1.getX()), (int) Math.rint(l.point1.getY()));
            }
        }
    }

    private void drawBorder(Border b, double w, double h, Graphics2D g) {
        Brush bg = b.getBackground();
        if (bg != null) {
            g.setColor(bg.toAwtColor());
            g.fillRect(0, 0, (int) Math.ceil(w), (int) Math.ceil(h));
        }
        Brush borderBrush = b.getBorderBrush();
        Thickness bt = b.getBorderThickness();
        if (borderBrush != null && bt != null && !bt.isEmpty()) {
            g.setColor(borderBrush.toAwtColor());
            float t = (float) Math.max(1, bt.getLeft());
            g.setStroke(new BasicStroke(t));
            double half = t / 2.0;
            double x = half;
            double y = half;
            double bw = Math.max(0, w - t);
            double bh = Math.max(0, h - t);
            if (b.getCornerRadius() != null) {
                double r = b.getCornerRadius().getTopLeft();
                g.draw(new RoundRectangle2D.Double(x, y, bw, bh, r * 2, r * 2));
            } else {
                g.draw(new Rectangle2D.Double(x, y, bw, bh));
            }
        }
    }

    private void drawTextBlock(TextBlock tb, double w, double h, Graphics2D g) {
        String text = tb.getText();
        if (text == null || text.isEmpty()) {
            return;
        }
        g.setFont(fontFor(tb.getFontFamily() == null ? null : tb.getFontFamily().getSource(),
                tb.getFontStyle(), tb.getFontWeight(), tb.getFontSize()));
        Brush fg = tb.getForeground();
        g.setColor(fg == null ? DEFAULT_FOREGROUND : fg.toAwtColor());
        FontMetrics fm = g.getFontMetrics();
        g.drawString(text, 0, fm.getAscent());
    }

    private void drawTextBox(TextBoxBase tb, double w, double h, Graphics2D g) {
        g.setColor(Color.WHITE);
        g.fillRect(0, 0, (int) Math.ceil(w), (int) Math.ceil(h));
        g.setColor(Color.GRAY);
        g.drawRect(0, 0, (int) Math.ceil(w) - 1, (int) Math.ceil(h) - 1);
        g.setFont(new Font(Font.DIALOG, Font.PLAIN, 12));
        g.setColor(DEFAULT_FOREGROUND);
        String text = tb instanceof TextBox ? ((TextBox) tb).getText() : "";
        FontMetrics fm = g.getFontMetrics();
        g.drawString(text, 3, fm.getAscent() + 2);
        if (tb instanceof TextBox && tb == focusedElement) {
            TextBox box = (TextBox) tb;
            int caret = Math.max(0, Math.min(box.getCaretIndex(), text.length()));
            int x = 3 + fm.stringWidth(text.substring(0, caret));
            g.drawLine(x, 2, x, (int) Math.max(4, h - 2));
        }
    }

    private void drawMenuItem(MenuItem item, double w, double h, Graphics2D g) {
        g.setColor(new Color(240, 240, 240));
        g.fillRect(0, 0, (int) Math.ceil(w), (int) Math.ceil(h));
        g.setColor(Color.GRAY);
        g.drawRect(0, 0, (int) Math.ceil(w) - 1, (int) Math.ceil(h) - 1);
        g.setFont(new Font(Font.DIALOG, Font.PLAIN, 12));
        g.setColor(DEFAULT_FOREGROUND);
        String text = item.getHeader() == null ? "" : String.valueOf(item.getHeader());
        FontMetrics fm = g.getFontMetrics();
        g.drawString(text, 7, fm.getAscent() + Math.max(0, (int) ((h - fm.getHeight()) / 2)));
        if (item.getItems().getCount() > 0) {
            g.drawString("\u203A", (int) Math.max(0, w - 14), fm.getAscent() + Math.max(0, (int) ((h - fm.getHeight()) / 2)));
        }
    }

    private void drawImage(Image image, double w, double h, Graphics2D g) {
        ImageSource src = image.getSource();
        if (!(src instanceof BitmapSource)) {
            return;
        }
        BufferedImage img = ((BitmapSource) src).getImage();
        if (img == null) {
            return;
        }
        double iw = img.getWidth();
        double ih = img.getHeight();
        double dw = w;
        double dh = h;
        Stretch stretch = image.getStretch() == null ? Stretch.Uniform : image.getStretch();
        if (stretch == Stretch.None) {
            dw = iw;
            dh = ih;
        } else if (stretch == Stretch.Uniform || stretch == Stretch.UniformToFill) {
            if (w > 0 && h > 0 && iw > 0 && ih > 0) {
                double sx = w / iw;
                double sy = h / ih;
                double s = stretch == Stretch.Uniform ? Math.min(sx, sy) : Math.max(sx, sy);
                dw = iw * s;
                dh = ih * s;
            }
        }
        g.drawImage(img, (int) Math.round((w - dw) / 2), (int) Math.round((h - dh) / 2),
                (int) Math.round(dw), (int) Math.round(dh), null);
    }

    private void drawShape(Shape s, Graphics2D g) {
        Geometry geom = s.getDefiningGeometry();
        java.awt.Shape awt = toAwtShape(geom);
        if (awt == null) {
            return;
        }
        applyGeometryTransform(geom, g);
        Brush fill = s.getFill();
        if (fill != null) {
            g.setColor(fill.toAwtColor());
            g.fill(awt);
        }
        Brush stroke = s.getStroke();
        double t = s.getStrokeThickness();
        if (stroke != null && t > 0) {
            g.setColor(stroke.toAwtColor());
            int cap = s.getStrokeStartLineCap() != null
                    && s.getStrokeStartLineCap().getValue() == 2 ? BasicStroke.CAP_ROUND : BasicStroke.CAP_BUTT;
            g.setStroke(new BasicStroke((float) t, cap, BasicStroke.JOIN_ROUND));
            g.draw(awt);
        }
    }

    private static void applyGeometryTransform(Geometry geom, Graphics2D g) {
        if (geom == null || geom.getTransform() == null) {
            return;
        }
        Matrix m = geom.getTransform() instanceof MatrixTransform
                ? ((MatrixTransform) geom.getTransform()).getMatrix()
                : null;
        if (m != null) {
            g.transform(new AffineTransform(m.getM11(), m.getM12(), m.getM21(), m.getM22(),
                    m.getOffsetX(), m.getOffsetY()));
        }
    }

    private static java.awt.Shape toAwtShape(Geometry geom) {
        if (geom == null || geom.isEmpty()) {
            return null;
        }
        if (geom instanceof RectangleGeometry) {
            RectangleGeometry rg = (RectangleGeometry) geom;
            Rect r = rg.getRect();
            if (r == null || r.isEmpty()) {
                return null;
            }
            if (rg.getRadiusX() > 0 || rg.getRadiusY() > 0) {
                return new RoundRectangle2D.Double(r.getX(), r.getY(), r.getWidth(), r.getHeight(),
                        rg.getRadiusX() * 2, rg.getRadiusY() * 2);
            }
            return new Rectangle2D.Double(r.getX(), r.getY(), r.getWidth(), r.getHeight());
        }
        if (geom instanceof EllipseGeometry) {
            EllipseGeometry eg = (EllipseGeometry) geom;
            Point c = eg.getCenter();
            if (c == null) {
                return null;
            }
            return new Ellipse2D.Double(c.getX() - eg.getRadiusX(), c.getY() - eg.getRadiusY(),
                    eg.getRadiusX() * 2, eg.getRadiusY() * 2);
        }
        if (geom instanceof StreamGeometry) {
            geom = ((StreamGeometry) geom).toPathGeometry();
        }
        if (geom instanceof PathGeometry) {
            return buildPath((PathGeometry) geom);
        }
        return null;
    }

    private static Path2D.Double buildPath(PathGeometry pg) {
        Path2D.Double p = new Path2D.Double(
                pg.getFillRule() == FillRule.Nonzero ? Path2D.WIND_NON_ZERO : Path2D.WIND_EVEN_ODD);
        for (PathFigure f : pg.getFigures()) {
            Point start = f.getStartPoint();
            if (start == null) {
                continue;
            }
            p.moveTo(start.getX(), start.getY());
            double curX = start.getX();
            double curY = start.getY();
            for (PathSegment seg : f.getSegments()) {
                if (seg instanceof LineSegment) {
                    Point pt = ((LineSegment) seg).getPoint();
                    if (pt != null) {
                        p.lineTo(pt.getX(), pt.getY());
                        curX = pt.getX();
                        curY = pt.getY();
                    }
                } else if (seg instanceof PolyLineSegment) {
                    for (Point pt : ((PolyLineSegment) seg).getPoints()) {
                        p.lineTo(pt.getX(), pt.getY());
                        curX = pt.getX();
                        curY = pt.getY();
                    }
                } else if (seg instanceof BezierSegment) {
                    BezierSegment b = (BezierSegment) seg;
                    if (b.getPoint3() != null) {
                        p.curveTo(b.getPoint1().getX(), b.getPoint1().getY(),
                                b.getPoint2().getX(), b.getPoint2().getY(),
                                b.getPoint3().getX(), b.getPoint3().getY());
                        curX = b.getPoint3().getX();
                        curY = b.getPoint3().getY();
                    }
                } else if (seg instanceof QuadraticBezierSegment) {
                    QuadraticBezierSegment q = (QuadraticBezierSegment) seg;
                    if (q.getPoint2() != null) {
                        p.quadTo(q.getPoint1().getX(), q.getPoint1().getY(),
                                q.getPoint2().getX(), q.getPoint2().getY());
                        curX = q.getPoint2().getX();
                        curY = q.getPoint2().getY();
                    }
                } else if (seg instanceof PolyBezierSegment) {
                    Point[] pts = ((PolyBezierSegment) seg).getPoints().toArray(new Point[0]);
                    for (int i = 0; i + 2 < pts.length; i += 3) {
                        p.curveTo(pts[i].getX(), pts[i].getY(),
                                pts[i + 1].getX(), pts[i + 1].getY(),
                                pts[i + 2].getX(), pts[i + 2].getY());
                        curX = pts[i + 2].getX();
                        curY = pts[i + 2].getY();
                    }
                } else if (seg instanceof PolyQuadraticBezierSegment) {
                    Point[] pts = ((PolyQuadraticBezierSegment) seg).getPoints().toArray(new Point[0]);
                    for (int i = 0; i + 1 < pts.length; i += 2) {
                        p.quadTo(pts[i].getX(), pts[i].getY(),
                                pts[i + 1].getX(), pts[i + 1].getY());
                        curX = pts[i + 1].getX();
                        curY = pts[i + 1].getY();
                    }
                } else if (seg instanceof ArcSegment) {
                    // compact approximation: arcs are drawn as straight segments
                    Point pt = ((ArcSegment) seg).getPoint();
                    if (pt != null) {
                        p.lineTo(pt.getX(), pt.getY());
                        curX = pt.getX();
                        curY = pt.getY();
                    }
                }
            }
            if (f.getIsClosed()) {
                p.closePath();
            }
        }
        return p;
    }

    private static Font fontFor(String family, FontStyle style, FontWeight weight, double size) {
        int styleBits = Font.PLAIN;
        if (style != null && style.getTag() != null
                && (FontStyle.ITALIC.equals(style.getTag()) || FontStyle.OBLIQUE.equals(style.getTag()))) {
            styleBits |= Font.ITALIC;
        }
        if (weight != null && weight.getWeight() >= 700) {
            styleBits |= Font.BOLD;
        }
        String name = family == null || family.trim().isEmpty() ? Font.DIALOG : family;
        double em = size > 0 ? size : 12;
        return new Font(name, styleBits, Math.max(1, (int) Math.rint(em)));
    }

    // ------------------------------------------------------------------
    // hit testing
    // ------------------------------------------------------------------

    private UIElement hitTest(double x, double y) {
        for (int i = hitOrder.size() - 1; i >= 0; i--) {
            UIElement e = hitOrder.get(i);
            if (!e.getIsHitTestVisible() || !e.getIsVisible()) {
                continue;
            }
            Rect r = bounds.get(e);
            if (r != null && r.contains(x, y)) {
                return e;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // mouse input routing
    // ------------------------------------------------------------------

    private void onMousePressed(MouseEvent e) {
        UIElement hit = hitTest(e.getX(), e.getY());
        if (hit == null) {
            return;
        }
        if (e.getButton() == MouseEvent.BUTTON1) {
            if (hit instanceof MenuItem) {
                MenuItem menuItem = (MenuItem) hit;
                if (menuItem.getItems().getCount() > 0) {
                    showMenuItems(menuItem, menuItem.getItems().getItems(), e);
                    return;
                }
            }
            requestFocusOn(hit);
            hit.raiseMouseLeftButtonDown(mouseButtonArgs(hit, e, MouseButton.Left, MouseButtonState.Pressed));
            if (hit instanceof MenuItem) {
                executeCommand((MenuItem) hit);
            }
        } else if (e.getButton() == MouseEvent.BUTTON3) {
            hit.raiseMouseRightButtonDown(mouseButtonArgs(hit, e, MouseButton.Right, MouseButtonState.Pressed));
            showContextMenu(hit, e);
        }
        Mouse.captureIf(hit);
    }

    private void showMenuItems(UIElement anchor, java.util.List<?> items, MouseEvent e) {
        JPopupMenu popup = new JPopupMenu();
        addMenuItems(popup, items);
        popup.show(this, e.getX(), e.getY());
    }

    private void addMenuItems(JComponent parent, java.util.List<?> items) {
        for (Object item : items) {
            if (item instanceof MenuItem) {
                MenuItem menuItem = (MenuItem) item;
                if (menuItem.getItems().getCount() > 0) {
                    JMenu sub = new JMenu(String.valueOf(menuItem.getHeader()));
                    addMenuItems(sub, menuItem.getItems().getItems());
                    parent.add(sub);
                } else {
                    JMenuItem leaf = new JMenuItem(String.valueOf(menuItem.getHeader()));
                    leaf.addActionListener(ev -> executeCommand(menuItem));
                    parent.add(leaf);
                }
            } else if (item != null) {
                parent.add(new JMenuItem(String.valueOf(item)));
            }
        }
    }

    private void executeCommand(MenuItem menuItem) {
        Object command = menuItem.getCommand();
        if (!(command instanceof dotnet.wpf.swing.input.RoutedUICommand) || owner == null) {
            return;
        }
        CommandBinding binding = owner.getCommandBindings().find(
                (dotnet.wpf.swing.input.RoutedUICommand) command);
        if (binding != null && binding.getExecuted() != null) {
            binding.getExecuted().accept(menuItem, new ExecutedRoutedEventArgs());
        }
    }

    private void onMouseReleased(MouseEvent e) {
        UIElement hit = hitTest(e.getX(), e.getY());
        if (hit == null) {
            return;
        }
        if (e.getButton() == MouseEvent.BUTTON1) {
            hit.raiseMouseLeftButtonUp(mouseButtonArgs(hit, e, MouseButton.Left, MouseButtonState.Released));
        } else if (e.getButton() == MouseEvent.BUTTON3) {
            hit.raiseMouseRightButtonUp(mouseButtonArgs(hit, e, MouseButton.Right, MouseButtonState.Released));
        }
    }

    private void onMouseMoved(MouseEvent e) {
        UIElement hit = hitTest(e.getX(), e.getY());
        if (hit != hoverElement) {
            if (hoverElement != null) {
                hoverElement.raiseMouseLeave(mouseMoveArgs(hoverElement, e));
            }
            hoverElement = hit;
            if (hit != null) {
                hit.raiseMouseEnter(mouseMoveArgs(hit, e));
            }
            setToolTipText(toolTipFor(hit));
        }
        if (hit != null) {
            hit.raiseMouseMove(mouseMoveArgs(hit, e));
        }
    }

    private void onMouseWheel(MouseWheelEvent e) {
        UIElement hit = hitTest(e.getX(), e.getY());
        if (hit == null) {
            return;
        }
        MouseWheelEventArgs args = new MouseWheelEventArgs();
        args.setDelta((int) Math.round(e.getPreciseWheelRotation() * 120));
        args.setPosition(new Point(e.getX(), e.getY()));
        args.setSource(hit);
        hit.raiseMouseWheel(args);
    }

    private static String toolTipFor(UIElement hit) {
        if (hit instanceof FrameworkElement) {
            Object tip = ((FrameworkElement) hit).getToolTip();
            if (tip != null) {
                return tip instanceof String ? (String) tip : String.valueOf(tip);
            }
        }
        return null;
    }

    private MouseButtonEventArgs mouseButtonArgs(UIElement hit, MouseEvent e,
                                                 MouseButton button, MouseButtonState state) {
        MouseButtonEventArgs args = new MouseButtonEventArgs();
        args.setChangedButton(button);
        args.setButtonState(state);
        args.setClickCount(e.getClickCount());
        args.setPosition(new Point(e.getX(), e.getY()));
        args.setSource(hit);
        int mods = e.getModifiersEx();
        args.setLeftButton((mods & java.awt.event.InputEvent.BUTTON1_DOWN_MASK) != 0
                ? MouseButtonState.Pressed : MouseButtonState.Released);
        args.setRightButton((mods & java.awt.event.InputEvent.BUTTON3_DOWN_MASK) != 0
                ? MouseButtonState.Pressed : MouseButtonState.Released);
        args.setMiddleButton((mods & java.awt.event.InputEvent.BUTTON2_DOWN_MASK) != 0
                ? MouseButtonState.Pressed : MouseButtonState.Released);
        return args;
    }

    private MouseEventArgs mouseMoveArgs(UIElement hit, MouseEvent e) {
        MouseEventArgs args = new MouseEventArgs();
        args.setPosition(new Point(e.getX(), e.getY()));
        args.setSource(hit);
        int mods = e.getModifiersEx();
        args.setLeftButton((mods & java.awt.event.InputEvent.BUTTON1_DOWN_MASK) != 0
                ? MouseButtonState.Pressed : MouseButtonState.Released);
        args.setRightButton((mods & java.awt.event.InputEvent.BUTTON3_DOWN_MASK) != 0
                ? MouseButtonState.Pressed : MouseButtonState.Released);
        args.setMiddleButton((mods & java.awt.event.InputEvent.BUTTON2_DOWN_MASK) != 0
                ? MouseButtonState.Pressed : MouseButtonState.Released);
        return args;
    }

    private void showContextMenu(UIElement hit, MouseEvent e) {
        ContextMenu menu = ContextMenuService.getContextMenu(hit);
        if (menu == null) {
            return;
        }
        JPopupMenu popup = new JPopupMenu();
        for (Object item : menu.getItems()) {
            if (item instanceof MenuItem) {
                MenuItem mi = (MenuItem) item;
                JMenuItem ji = new JMenuItem(String.valueOf(mi.getHeader()));
                ji.addActionListener(ev -> mi.raiseClick());
                popup.add(ji);
            } else if (item != null) {
                popup.add(new JMenuItem(String.valueOf(item)));
            }
        }
        popup.addPopupMenuListener(new PopupMenuListener() {
            @Override
            public void popupMenuWillBecomeVisible(PopupMenuEvent event) {
            }

            @Override
            public void popupMenuWillBecomeInvisible(PopupMenuEvent event) {
                menu.raiseClosed();
            }

            @Override
            public void popupMenuCanceled(PopupMenuEvent event) {
                menu.raiseClosed();
            }
        });
        popup.show(this, e.getX(), e.getY());
    }

    // ------------------------------------------------------------------
    // keyboard input routing
    // ------------------------------------------------------------------

    private void onKeyPressed(KeyEvent e) {
        if (focusedElement == null) {
            if (owner != null && handleInputBinding(e)) {
                e.consume();
            }
            return;
        }
        KeyEventArgs args = new KeyEventArgs();
        args.setKey(Key.fromAwtKeyCode(e.getKeyCode()));
        args.setSource(focusedElement);
        if (owner != null && handleInputBinding(e)) {
            e.consume();
            return;
        }
        focusedElement.raiseKeyDown(args);
        handleTextBoxKey(focusedElement, e);
    }

    private boolean handleInputBinding(KeyEvent e) {
        int modifiers = 0;
        if (e.isControlDown()) {
            modifiers |= ModifierKeys.Control;
        }
        if (e.isShiftDown()) {
            modifiers |= ModifierKeys.Shift;
        }
        if (e.isAltDown()) {
            modifiers |= ModifierKeys.Alt;
        }
        InputBinding binding = owner.getInputBindings().find(
                Key.fromAwtKeyCode(e.getKeyCode()), modifiers);
        if (binding == null) {
            return false;
        }
        CommandBinding commandBinding = owner.getCommandBindings().find(binding.getCommand());
        if (commandBinding != null && commandBinding.getExecuted() != null) {
            commandBinding.getExecuted().accept(
                    focusedElement != null ? focusedElement : owner, new ExecutedRoutedEventArgs());
            return true;
        }
        return false;
    }

    private void onKeyReleased(KeyEvent e) {
        if (focusedElement == null) {
            return;
        }
        KeyEventArgs args = new KeyEventArgs();
        args.setKey(Key.fromAwtKeyCode(e.getKeyCode()));
        args.setSource(focusedElement);
        focusedElement.raiseKeyUp(args);
    }

    private void onKeyTyped(KeyEvent e) {
        if (focusedElement instanceof TextBox) {
            TextBox box = (TextBox) focusedElement;
            char c = e.getKeyChar();
            if (!Character.isISOControl(c)) {
                insertText(box, String.valueOf(c));
            }
        }
    }

    private static void handleTextBoxKey(UIElement focused, KeyEvent e) {
        if (!(focused instanceof TextBox)) {
            return;
        }
        TextBox box = (TextBox) focused;
        String text = box.getText() == null ? "" : box.getText();
        int caret = Math.max(0, Math.min(box.getCaretIndex(), text.length()));
        switch (e.getKeyCode()) {
            case KeyEvent.VK_BACK_SPACE:
                if (caret > 0) {
                    box.setText(text.substring(0, caret - 1) + text.substring(caret));
                    box.setCaretIndex(caret - 1);
                    refresh(box);
                }
                break;
            case KeyEvent.VK_DELETE:
                if (caret < text.length()) {
                    box.setText(text.substring(0, caret) + text.substring(caret + 1));
                    refresh(box);
                }
                break;
            case KeyEvent.VK_LEFT:
                box.setCaretIndex(Math.max(0, caret - 1));
                refresh(box);
                break;
            case KeyEvent.VK_RIGHT:
                box.setCaretIndex(Math.min(text.length(), caret + 1));
                refresh(box);
                break;
            default:
                break;
        }
    }

    private static void insertText(TextBox box, String s) {
        String text = box.getText() == null ? "" : box.getText();
        int caret = Math.max(0, Math.min(box.getCaretIndex(), text.length()));
        box.setText(text.substring(0, caret) + s + text.substring(caret));
        box.setCaretIndex(caret + s.length());
        refresh(box);
    }

    /** Internal mouse-capture helper (no-op unless an element is hit). */
    private static final class Mouse {
        static void captureIf(UIElement hit) {
            if (hit != null) {
                dotnet.wpf.swing.input.Mouse.capture(hit);
            }
        }
    }
}
