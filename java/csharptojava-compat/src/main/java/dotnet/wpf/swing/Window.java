package dotnet.wpf.swing;

import dotnet.wpf.swing.internal.ScenePanel;
import dotnet.wpf.swing.internal.UiLoop;
import io.github.ningpp.compat.CancelEventArgs;

import javax.swing.JDialog;
import javax.swing.JFrame;
import java.awt.event.WindowAdapter;
import java.awt.event.WindowEvent;
import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

/**
 * System.Windows.Window compatibility class (Swing-backed).
 *
 * <p>{@link #show()} displays a real Swing {@link JFrame} whose content pane
 * renders the WPF-style content tree; {@link #close()}/{@link #hide()}
 * tear it down. Implements the real window lifecycle: {@code SourceInitialized},
 * {@code Activated}/{@code Deactivated} (driven by the native frame's
 * activation), {@code ContentRendered} (after the first paint), cancelable
 * {@code Closing}, {@code Closed}, plus {@code StateChanged}/
 * {@code LocationChanged} driven by {@code WindowState}/{@code Left}/{@code Top}.
 * The element tree gets {@code Loaded} on each show and {@code Unloaded} on
 * hide/close. Closing the last window of an {@link Application} shuts the
 * application down according to its {@link ShutdownMode}. In headless
 * environments the window state is tracked but no native frame is created.</p>
 */
public class Window extends ContentControl {

    private static final List<Window> OPEN_WINDOWS = new ArrayList<>();

    private String title = "";
    private boolean isVisible;
    private boolean isActive;
    private JFrame frame;
    private ScenePanel scenePanel;

    private WindowState windowState = WindowState.Normal;
    private ResizeMode resizeMode = ResizeMode.CanResize;
    private WindowStartupLocation windowStartupLocation = WindowStartupLocation.Manual;
    private WindowStyle windowStyle = WindowStyle.SingleBorderWindow;
    private boolean showActivated = true;
    private boolean showInTaskbar = true;
    private boolean topmost;
    private Window owner;
    private Boolean dialogResult;
    private double left = Double.NaN;
    private double top = Double.NaN;

    private final List<BiConsumer<Object, RoutedEventArgs>> closedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, CancelEventArgs>> closingListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> contentRenderedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> sourceInitializedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> activatedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> deactivatedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> stateChangedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> locationChangedListeners = new ArrayList<>();

    public String getTitle() {
        return title;
    }

    public void setTitle(String value) {
        title = value == null ? "" : value;
        if (frame != null) {
            UiLoop.get().invokeLater(() -> frame.setTitle(title));
        }
    }

    public boolean getIsVisible() {
        return isVisible;
    }

    public boolean getIsActive() {
        return isActive;
    }

    public WindowState getWindowState() {
        return windowState;
    }

    public void setWindowState(WindowState value) {
        if (value == null || value == windowState) {
            return;
        }
        windowState = value;
        if (frame != null) {
            applyWindowState(frame);
        }
        raiseStateChanged();
    }

    public ResizeMode getResizeMode() {
        return resizeMode;
    }

    public void setResizeMode(ResizeMode value) {
        resizeMode = value == null ? ResizeMode.CanResize : value;
        if (frame != null) {
            UiLoop.get().invokeLater(() -> frame.setResizable(
                    resizeMode == ResizeMode.CanResize || resizeMode == ResizeMode.CanResizeWithGrip));
        }
    }

    public WindowStartupLocation getWindowStartupLocation() {
        return windowStartupLocation;
    }

    public void setWindowStartupLocation(WindowStartupLocation value) {
        windowStartupLocation = value == null ? WindowStartupLocation.Manual : value;
    }

    public boolean getShowActivated() {
        return showActivated;
    }

    public void setShowActivated(boolean value) {
        showActivated = value;
    }

    public boolean getTopmost() {
        return topmost;
    }

    public void setTopmost(boolean value) {
        topmost = value;
        if (frame != null) {
            UiLoop.get().invokeLater(() -> frame.setAlwaysOnTop(topmost));
        }
    }

    public Window getOwner() {
        return owner;
    }

    public void setOwner(Window value) {
        owner = value;
        if (frame != null && owner != null && owner.frame != null) {
            UiLoop.get().invokeLater(() -> frame.setLocationRelativeTo(owner.frame));
        }
    }

    public boolean getShowInTaskbar() {
        return showInTaskbar;
    }

    public void setShowInTaskbar(boolean value) {
        showInTaskbar = value;
        if (frame != null) {
            UiLoop.get().invokeLater(() -> frame.setType(
                    showInTaskbar ? java.awt.Window.Type.NORMAL : java.awt.Window.Type.UTILITY));
        }
    }

    public WindowStyle getWindowStyle() {
        return windowStyle;
    }

    public void setWindowStyle(WindowStyle value) {
        windowStyle = value == null ? WindowStyle.SingleBorderWindow : value;
        if (frame != null) {
            UiLoop.get().invokeLater(() -> {
                frame.setUndecorated(windowStyle == WindowStyle.None);
                frame.setType(windowStyle == WindowStyle.ToolWindow
                        ? java.awt.Window.Type.UTILITY : java.awt.Window.Type.NORMAL);
            });
        }
    }

    public Boolean getDialogResult() {
        return dialogResult;
    }

    public void setDialogResult(Boolean value) {
        dialogResult = value;
    }

    public double getLeft() {
        return left;
    }

    public void setLeft(double value) {
        if (left == value) {
            return;
        }
        left = value;
        if (frame != null) {
            UiLoop.get().invokeLater(() -> frame.setLocation((int) Math.rint(left), frame.getY()));
        }
        raiseLocationChanged();
    }

    public double getTop() {
        return top;
    }

    public void setTop(double value) {
        if (top == value) {
            return;
        }
        top = value;
        if (frame != null) {
            UiLoop.get().invokeLater(() -> frame.setLocation(frame.getX(), (int) Math.rint(top)));
        }
        raiseLocationChanged();
    }

    public void show() {
        if (isVisible) {
            return;
        }
        isVisible = true;
        synchronized (OPEN_WINDOWS) {
            OPEN_WINDOWS.add(this);
        }
        if (UiLoop.isHeadless()) {
            raiseSourceInitialized();
            raiseWindowLoaded();
            raiseContentRendered();
            raiseActivated();
            return;
        }
        UiLoop.get().invokeAndWait(() -> {
            if (frame == null) {
                frame = new JFrame(title);
                scenePanel = new ScenePanel(this);
                scenePanel.setRoot(this);
                frame.setContentPane(scenePanel);
                frame.setDefaultCloseOperation(JFrame.DO_NOTHING_ON_CLOSE);
                frame.setUndecorated(windowStyle == WindowStyle.None);
                frame.setType(!showInTaskbar || windowStyle == WindowStyle.ToolWindow
                        ? java.awt.Window.Type.UTILITY : java.awt.Window.Type.NORMAL);
                frame.addWindowListener(new WindowAdapter() {
                    @Override
                    public void windowClosing(WindowEvent e) {
                        close();
                    }

                    @Override
                    public void windowActivated(WindowEvent e) {
                        raiseActivated();
                    }

                    @Override
                    public void windowDeactivated(WindowEvent e) {
                        raiseDeactivated();
                    }

                    @Override
                    public void windowClosed(WindowEvent e) {
                        frame = null;
                    }
                });
                frame.addComponentListener(new java.awt.event.ComponentAdapter() {
                    @Override
                    public void componentMoved(java.awt.event.ComponentEvent e) {
                        left = frame.getX();
                        top = frame.getY();
                        raiseLocationChanged();
                    }
                });
            } else {
                frame.setTitle(title);
            }
            applyWindowState(frame);
            frame.setResizable(resizeMode == ResizeMode.CanResize || resizeMode == ResizeMode.CanResizeWithGrip);
            frame.setAlwaysOnTop(topmost);
            frame.setFocusableWindowState(showActivated);
            applyWindowSize(frame);
            applyStartupLocation(frame);
            raiseSourceInitialized();
            frame.setVisible(true);
        });
    }

    public void hide() {
        if (!isVisible) {
            return;
        }
        isVisible = false;
        synchronized (OPEN_WINDOWS) {
            OPEN_WINDOWS.remove(this);
        }
        raiseWindowUnloaded();
        if (frame == null && isActive) {
            raiseDeactivated();
        }
        if (frame != null) {
            UiLoop.get().invokeAndWait(() -> {
                frame.setVisible(false);
                frame.dispose();
            });
            frame = null;
        }
    }

    public void close() {
        if (!isVisible) {
            return;
        }
        boolean appShuttingDown = UiLoop.get().isShuttingDown();
        if (!appShuttingDown && !raiseClosing()) {
            return;
        }
        hide();
        raiseClosed();
        Application app = Application.getCurrent();
        if (app != null) {
            app.windowClosed(this);
        }
    }

    /**
     * Displays the window as a modal dialog and blocks until it is closed.
     */
    public Boolean showDialog() {
        if (isVisible) {
            return true;
        }
        isVisible = true;
        synchronized (OPEN_WINDOWS) {
            OPEN_WINDOWS.add(this);
        }
        if (UiLoop.isHeadless()) {
            raiseSourceInitialized();
            raiseWindowLoaded();
            raiseContentRendered();
            raiseActivated();
            return dialogResult != null ? dialogResult : true;
        }
        JFrame ownerFrame = frame;
        UiLoop.get().invokeAndWait(() -> {
            JDialog dialog = new JDialog(ownerFrame, title, true);
            scenePanel = new ScenePanel(this);
            scenePanel.setRoot(this);
            dialog.setContentPane(scenePanel);
            applyWindowSize(dialog);
            dialog.setDefaultCloseOperation(JDialog.DO_NOTHING_ON_CLOSE);
            dialog.addWindowListener(new WindowAdapter() {
                @Override
                public void windowClosing(WindowEvent e) {
                    dialog.setVisible(false);
                }

                @Override
                public void windowClosed(WindowEvent e) {
                    finishDialog();
                }
            });
            raiseSourceInitialized();
            dialog.setVisible(true);
        });
        return dialogResult != null ? dialogResult : true;
    }

    private void finishDialog() {
        if (!isVisible) {
            return;
        }
        isVisible = false;
        synchronized (OPEN_WINDOWS) {
            OPEN_WINDOWS.remove(this);
        }
        raiseWindowUnloaded();
        raiseDeactivated();
        raiseClosed();
        scenePanel = null;
    }

    public void initializeComponent() {
        // XAML layout is not loaded in the Swing-backed compat layer.
    }

    // ---- window events ----

    public void addClosedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            closedListeners.add(handler);
        }
    }

    public void removeClosedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        closedListeners.remove(handler);
    }

    public void addClosingListener(BiConsumer<Object, CancelEventArgs> handler) {
        if (handler != null) {
            closingListeners.add(handler);
        }
    }

    public void removeClosingListener(BiConsumer<Object, CancelEventArgs> handler) {
        closingListeners.remove(handler);
    }

    public void addContentRenderedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            contentRenderedListeners.add(handler);
        }
    }

    public void removeContentRenderedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        contentRenderedListeners.remove(handler);
    }

    public void addSourceInitializedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            sourceInitializedListeners.add(handler);
        }
    }

    public void removeSourceInitializedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        sourceInitializedListeners.remove(handler);
    }

    public void addActivatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            activatedListeners.add(handler);
        }
    }

    public void removeActivatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        activatedListeners.remove(handler);
    }

    public void addDeactivatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            deactivatedListeners.add(handler);
        }
    }

    public void removeDeactivatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        deactivatedListeners.remove(handler);
    }

    public void addStateChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            stateChangedListeners.add(handler);
        }
    }

    public void removeStateChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        stateChangedListeners.remove(handler);
    }

    public void addLocationChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            locationChangedListeners.add(handler);
        }
    }

    public void removeLocationChangedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        locationChangedListeners.remove(handler);
    }

    // ---- protected hooks (System.Windows.Window.OnXxx counterparts) ----

    protected void onSourceInitialized() {
    }

    protected void onActivated() {
    }

    protected void onDeactivated() {
    }

    protected void onStateChanged() {
    }

    protected void onLocationChanged() {
    }

    protected void onClosing(CancelEventArgs e) {
    }

    protected void onClosed() {
    }

    protected void onContentRendered() {
    }

    // ---- raising ----

    /** Raised by the renderer once the window content has been drawn. */
    public void raiseContentRendered() {
        onContentRendered();
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(contentRenderedListeners)) {
            h.accept(this, e);
        }
    }

    public void raiseSourceInitialized() {
        onSourceInitialized();
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(sourceInitializedListeners)) {
            h.accept(this, e);
        }
    }

    /** Internal: raised by the native frame's windowActivated event. */
    public void raiseActivated() {
        isActive = true;
        onActivated();
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(activatedListeners)) {
            h.accept(this, e);
        }
        Application app = Application.getCurrent();
        if (app != null) {
            app.windowActivated(this);
        }
    }

    /** Internal: raised by the native frame's windowDeactivated event. */
    public void raiseDeactivated() {
        isActive = false;
        onDeactivated();
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(deactivatedListeners)) {
            h.accept(this, e);
        }
        Application app = Application.getCurrent();
        if (app != null) {
            app.windowDeactivated(this);
        }
    }

    private void raiseStateChanged() {
        onStateChanged();
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(stateChangedListeners)) {
            h.accept(this, e);
        }
    }

    private void raiseLocationChanged() {
        onLocationChanged();
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(locationChangedListeners)) {
            h.accept(this, e);
        }
    }

    /** Runs the Closing lifecycle; returns false when a listener cancelled the close. */
    private boolean raiseClosing() {
        CancelEventArgs e = new CancelEventArgs();
        onClosing(e);
        for (BiConsumer<Object, CancelEventArgs> h : new ArrayList<>(closingListeners)) {
            h.accept(this, e);
        }
        return !e.getCancel();
    }

    private void raiseClosed() {
        onClosed();
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(closedListeners)) {
            h.accept(this, e);
        }
    }

    private void raiseWindowLoaded() {
        raiseLoaded();
        if (getContent() instanceof UIElement) {
            dotnet.wpf.swing.internal.ScenePanel.raiseLoadedTree((UIElement) getContent());
        }
    }

    private void raiseWindowUnloaded() {
        if (scenePanel != null) {
            scenePanel.unloadTree();
        } else {
            dotnet.wpf.swing.internal.ScenePanel.raiseUnloadedTree(this);
        }
    }

    private void applyWindowSize(java.awt.Window w) {
        double width = getWidth();
        double height = getHeight();
        int wPx = Double.isNaN(width) || width <= 0 ? 800 : (int) Math.rint(width);
        int hPx = Double.isNaN(height) || height <= 0 ? 600 : (int) Math.rint(height);
        w.setSize(wPx, hPx);
    }

    private void applyWindowState(java.awt.Window w) {
        if (!(w instanceof java.awt.Frame)) {
            return;
        }
        java.awt.Frame f = (java.awt.Frame) w;
        int state = java.awt.Frame.NORMAL;
        if (windowState == WindowState.Maximized) {
            state = java.awt.Frame.MAXIMIZED_BOTH;
        } else if (windowState == WindowState.Minimized) {
            state = java.awt.Frame.ICONIFIED;
        }
        f.setExtendedState(state);
    }

    private void applyStartupLocation(java.awt.Window w) {
        if (windowStartupLocation == WindowStartupLocation.CenterScreen) {
            w.setLocationRelativeTo(null);
        } else if (windowStartupLocation == WindowStartupLocation.CenterOwner) {
            java.awt.Window owner = frame;
            w.setLocationRelativeTo(owner == w ? null : owner);
        } else if (!Double.isNaN(left) && !Double.isNaN(top)) {
            w.setLocation((int) Math.rint(left), (int) Math.rint(top));
        }
    }

    /** Owner frame used by modal dialogs (MessageBox, showDialog). */
    javax.swing.JFrame getOwnerFrame() {
        return frame;
    }

    /** Number of currently open windows (used by Application.shutdown heuristics). */
    static int getOpenWindowCount() {
        synchronized (OPEN_WINDOWS) {
            return OPEN_WINDOWS.size();
        }
    }

    /** Snapshot of the currently open windows (used by {@link Application#getWindows()}). */
    static List<Window> getOpenWindows() {
        synchronized (OPEN_WINDOWS) {
            return new ArrayList<>(OPEN_WINDOWS);
        }
    }

    /** True when any open window currently holds activation. */
    static boolean hasAnyActiveWindow() {
        synchronized (OPEN_WINDOWS) {
            for (Window w : OPEN_WINDOWS) {
                if (w.isActive) {
                    return true;
                }
            }
        }
        return false;
    }

    /** Closes every open window (used by Application.shutdown). */
    static void closeAll() {
        List<Window> snapshot;
        synchronized (OPEN_WINDOWS) {
            snapshot = new ArrayList<>(OPEN_WINDOWS);
        }
        for (Window w : snapshot) {
            w.close();
        }
    }
}
