package dotnet.wpf.swing;

import dotnet.wpf.swing.internal.UiLoop;

import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

/**
 * System.Windows.Application compatibility class.
 * Generated code subclasses this (e.g. {@code class App extends Application}).
 *
 * <p>Swing-backed: {@link #run()} raises the Startup event, calls the
 * overridable {@link #onStartup(StartupEventArgs)} hook, shows the main
 * window and blocks on the UI loop until {@link #shutdown()} is requested
 * (closing the main window also shuts the app down). In headless
 * environments (tests, CI) the startup hook still runs but no UI loop is
 * started.</p>
 */
public class Application {

    private static Application current;
    private Window mainWindow;

    private final List<BiConsumer<Object, StartupEventArgs>> startupListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> exitListeners = new ArrayList<>();
    private boolean shutdownRequested;

    public Application() {
        current = this;
    }

    /** Runs the application with no command-line arguments. */
    public int run() {
        return run(new StartupEventArgs(new String[0]));
    }

    /** Runs the application with the given command-line arguments. */
    public int run(String[] args) {
        return run(new StartupEventArgs(args));
    }

    public int run(StartupEventArgs e) {
        if (UiLoop.isHeadless()) {
            raiseStartup(e);
            onStartup(e);
            Window mw = getMainWindow();
            if (mw != null && !mw.getIsVisible()) {
                mw.show();
            }
            return 0;
        }
        UiLoop loop = UiLoop.get();
        loop.invokeAndWait(() -> {
            raiseStartup(e);
            onStartup(e);
        });
        Window mw = getMainWindow();
        if (mw != null && !mw.getIsVisible()) {
            loop.invokeAndWait(mw::show);
        }
        loop.awaitShutdown();
        raiseExit();
        return 0;
    }

    /** Startup event (raised before the {@code onStartup} hook). */
    public void addStartupListener(BiConsumer<Object, StartupEventArgs> handler) {
        if (handler != null) {
            startupListeners.add(handler);
        }
    }

    public void removeStartupListener(BiConsumer<Object, StartupEventArgs> handler) {
        startupListeners.remove(handler);
    }

    /** Exit event (raised after the UI loop ends). */
    public void addExitListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            exitListeners.add(handler);
        }
    }

    public void removeExitListener(BiConsumer<Object, RoutedEventArgs> handler) {
        exitListeners.remove(handler);
    }

    protected void onStartup(StartupEventArgs e) {
        // overridable hook
    }

    /** Requests application shutdown: stops the UI loop and closes all windows. */
    public void shutdown() {
        if (shutdownRequested) {
            return;
        }
        shutdownRequested = true;
        UiLoop loop = UiLoop.get();
        loop.shutdown();
        loop.invokeAndWait(Window::closeAll);
    }

    public Window getMainWindow() {
        return mainWindow;
    }

    public void setMainWindow(Window value) {
        mainWindow = value;
    }

    public static Application getCurrent() {
        return current;
    }

    public static void loadComponent(Window window, Object resourceLocator) {
        // XAML resource loading is not supported in the Swing-backed compat layer.
    }

    private void raiseStartup(StartupEventArgs e) {
        for (BiConsumer<Object, StartupEventArgs> h : new ArrayList<>(startupListeners)) {
            h.accept(this, e);
        }
    }

    private void raiseExit() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(exitListeners)) {
            h.accept(this, e);
        }
    }
}