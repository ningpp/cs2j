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
 * window and blocks on the UI loop until {@link #shutdown()} is requested.
 * Implements the real application lifecycle: {@code Activated}/
 * {@code Deactivated} follow the active window, {@code Exit}/{@code onExit}
 * run after the loop ends, {@link #getShutdownMode()} controls whether
 * closing the last/main window shuts the app down, and unhandled UI-thread
 * exceptions are routed to {@code DispatcherUnhandledException}. In headless
 * environments (tests, CI) the startup hook still runs but no UI loop is
 * started.</p>
 */
public class Application {

    private static Application current;
    private Window mainWindow;
    private ShutdownMode shutdownMode = ShutdownMode.OnLastWindowClose;
    private int exitCode;
    private String startupUri;
    private boolean isActive;

    private final List<BiConsumer<Object, StartupEventArgs>> startupListeners = new ArrayList<>();
    private final List<BiConsumer<Object, ExitEventArgs>> exitListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> activatedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> deactivatedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, SessionEndingCancelEventArgs>> sessionEndingListeners = new ArrayList<>();
    private final List<BiConsumer<Object, Throwable>> dispatcherUnhandledExceptionListeners = new ArrayList<>();
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
            raiseExit();
            return exitCode;
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
        return exitCode;
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
    public void addExitListener(BiConsumer<Object, ExitEventArgs> handler) {
        if (handler != null) {
            exitListeners.add(handler);
        }
    }

    public void removeExitListener(BiConsumer<Object, ExitEventArgs> handler) {
        exitListeners.remove(handler);
    }

    /** Raised when any window of the application becomes active. */
    public void addActivatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            activatedListeners.add(handler);
        }
    }

    public void removeActivatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        activatedListeners.remove(handler);
    }

    /** Raised when the last active window of the application deactivates. */
    public void addDeactivatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        if (handler != null) {
            deactivatedListeners.add(handler);
        }
    }

    public void removeDeactivatedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        deactivatedListeners.remove(handler);
    }

    /** Raised when the OS session ends; cancelable via {@link SessionEndingCancelEventArgs#setCancel(boolean)}. */
    public void addSessionEndingListener(BiConsumer<Object, SessionEndingCancelEventArgs> handler) {
        if (handler != null) {
            sessionEndingListeners.add(handler);
        }
    }

    public void removeSessionEndingListener(BiConsumer<Object, SessionEndingCancelEventArgs> handler) {
        sessionEndingListeners.remove(handler);
    }

    /** Raised when an unhandled exception escapes the UI thread. */
    public void addDispatcherUnhandledExceptionListener(BiConsumer<Object, Throwable> handler) {
        if (handler != null) {
            dispatcherUnhandledExceptionListeners.add(handler);
        }
    }

    public void removeDispatcherUnhandledExceptionListener(BiConsumer<Object, Throwable> handler) {
        dispatcherUnhandledExceptionListeners.remove(handler);
    }

    protected void onStartup(StartupEventArgs e) {
        // overridable hook
    }

    protected void onExit(ExitEventArgs e) {
        // overridable hook
    }

    /** System.Windows.Application.OnSessionEnding counterpart. */
    protected void onSessionEnding(SessionEndingCancelEventArgs e) {
        // overridable hook
    }

    public ShutdownMode getShutdownMode() {
        return shutdownMode;
    }

    public void setShutdownMode(ShutdownMode value) {
        shutdownMode = value == null ? ShutdownMode.OnLastWindowClose : value;
    }

    public int getExitCode() {
        return exitCode;
    }

    public void setExitCode(int value) {
        exitCode = value;
    }

    /** URI of the window shown automatically by {@link #run(StartupEventArgs)} (XAML loading not supported). */
    public String getStartupUri() {
        return startupUri;
    }

    public void setStartupUri(String value) {
        startupUri = value;
    }

    /** Snapshot of the application's open windows. */
    public WindowCollection getWindows() {
        return new WindowCollection(Window.getOpenWindows());
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

    /** Shuts the application down with an exit code. */
    public void shutdown(int exitCode) {
        this.exitCode = exitCode;
        shutdown();
    }

    /** Internal: called by {@link Window#close()} to apply {@link ShutdownMode}. */
    void windowClosed(Window window) {
        if (shutdownRequested) {
            return;
        }
        if (shutdownMode == ShutdownMode.OnMainWindowClose) {
            if (window == mainWindow) {
                shutdown();
            }
        } else if (shutdownMode == ShutdownMode.OnLastWindowClose) {
            if (Window.getOpenWindowCount() == 0) {
                shutdown();
            }
        }
    }

    /** Internal: called by {@link Window#raiseActivated()}. */
    void windowActivated(Window window) {
        if (isActive) {
            return;
        }
        isActive = true;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(activatedListeners)) {
            h.accept(this, e);
        }
    }

    /** Internal: called by {@link Window#raiseDeactivated()}; app deactivates when no window is active. */
    void windowDeactivated(Window window) {
        if (!isActive) {
            return;
        }
        if (Window.hasAnyActiveWindow()) {
            return;
        }
        isActive = false;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(deactivatedListeners)) {
            h.accept(this, e);
        }
    }

    /** True while any window of the application holds activation. */
    boolean isActive() {
        return isActive;
    }

    /** Raises the cancelable SessionEnding lifecycle (not fired automatically in the Swing-backed layer). */
    public void raiseSessionEnding(SessionEndingCancelEventArgs.ReasonSessionEnding reason) {
        SessionEndingCancelEventArgs e = new SessionEndingCancelEventArgs(reason);
        onSessionEnding(e);
        for (BiConsumer<Object, SessionEndingCancelEventArgs> h : new ArrayList<>(sessionEndingListeners)) {
            h.accept(this, e);
        }
    }

    /** Internal: routes an unhandled UI-thread exception to the DispatcherUnhandledException listeners. */
    public void raiseDispatcherUnhandledException(Throwable error) {
        for (BiConsumer<Object, Throwable> h : new ArrayList<>(dispatcherUnhandledExceptionListeners)) {
            try {
                h.accept(this, error);
            } catch (Throwable ignored) {
                // listener errors must not break the exception path
            }
        }
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
        dotnet.wpf.swing.internal.XamlLoader.loadComponent(window, resourceLocator);
    }

    private void raiseStartup(StartupEventArgs e) {
        for (BiConsumer<Object, StartupEventArgs> h : new ArrayList<>(startupListeners)) {
            h.accept(this, e);
        }
    }

    private void raiseExit() {
        ExitEventArgs e = new ExitEventArgs(exitCode);
        onExit(e);
        for (BiConsumer<Object, ExitEventArgs> h : new ArrayList<>(exitListeners)) {
            h.accept(this, e);
        }
        exitCode = e.getExitCode();
    }
}
