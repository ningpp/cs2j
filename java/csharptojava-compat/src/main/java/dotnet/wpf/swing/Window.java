package dotnet.wpf.swing;

import dotnet.wpf.swing.internal.ScenePanel;
import dotnet.wpf.swing.internal.UiLoop;

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
 * tear it down. Closing the main window of an {@link Application} shuts the
 * application down. In headless environments the window state is tracked
 * but no native frame is created.</p>
 */
public class Window extends ContentControl {

    private static final List<Window> OPEN_WINDOWS = new ArrayList<>();

    private String title = "";
    private boolean isVisible;
    private JFrame frame;
    private ScenePanel scenePanel;

    private final List<BiConsumer<Object, RoutedEventArgs>> closedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> closingListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> contentRenderedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> sourceInitializedListeners = new ArrayList<>();

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
            raiseContentRendered();
            raiseWindowLoaded();
            return;
        }
        UiLoop.get().invokeAndWait(() -> {
            if (frame == null) {
                frame = new JFrame(title);
                scenePanel = new ScenePanel(this);
                scenePanel.setRoot(this);
                frame.setContentPane(scenePanel);
                frame.setDefaultCloseOperation(JFrame.DO_NOTHING_ON_CLOSE);
                frame.addWindowListener(new WindowAdapter() {
                    @Override
                    public void windowClosing(WindowEvent e) {
                        close();
                    }
                });
            } else {
                frame.setTitle(title);
            }
            applyWindowSize(frame);
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
        if (frame != null) {
            UiLoop.get().invokeAndWait(() -> frame.setVisible(false));
        }
    }

    public void close() {
        if (!isVisible) {
            return;
        }
        raiseClosing();
        hide();
        raiseClosed();
        Application app = Application.getCurrent();
        if (app != null && app.getMainWindow() == this) {
            app.shutdown();
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
            raiseContentRendered();
            raiseWindowLoaded();
            return true;
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
        return true;
    }

    private void finishDialog() {
        if (!isVisible) {
            return;
        }
        isVisible = false;
        synchronized (OPEN_WINDOWS) {
            OPEN_WINDOWS.remove(this);
        }
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

    public void addClosingListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            closingListeners.add(handler);
        }
    }

    public void removeClosingListener(BiConsumer<Object, RoutedEventArgs> handler) {
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

    /** Raised by the renderer once the window content has been drawn. */
    public void raiseContentRendered() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(contentRenderedListeners)) {
            h.accept(this, e);
        }
    }

    public void raiseSourceInitialized() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(sourceInitializedListeners)) {
            h.accept(this, e);
        }
    }

    private void raiseClosing() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(closingListeners)) {
            h.accept(this, e);
        }
    }

    private void raiseClosed() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(closedListeners)) {
            h.accept(this, e);
        }
    }

    private void raiseWindowLoaded() {
        if (getContent() instanceof FrameworkElement) {
            ((FrameworkElement) getContent()).raiseLoaded();
        }
    }

    private void applyWindowSize(java.awt.Window w) {
        double width = getWidth();
        double height = getHeight();
        int wPx = Double.isNaN(width) || width <= 0 ? 800 : (int) Math.rint(width);
        int hPx = Double.isNaN(height) || height <= 0 ? 600 : (int) Math.rint(height);
        w.setSize(wPx, hPx);
    }

    /** Owner frame used by modal dialogs (MessageBox, showDialog). */
    javax.swing.JFrame getOwnerFrame() {
        return frame;
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