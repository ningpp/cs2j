package dotnet.wpf.swing;

import dotnet.wpf.swing.internal.UiLoop;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Headless application lifecycle tests: startup/exit, shutdown modes,
 * window tracking, session ending and activation semantics.
 */
class ApplicationTest {

    @BeforeEach
    @AfterEach
    void resetLoop() {
        UiLoop.get().reset();
    }

    @Test
    void run_raisesStartupThenExit() {
        Application app = new Application();
        List<String> events = new ArrayList<>();
        app.addStartupListener((s, e) -> events.add("Startup"));
        app.addExitListener((s, e) -> events.add("Exit"));
        app.setExitCode(42);

        int code = app.run();

        assertEquals(List.of("Startup", "Exit"), events);
        assertEquals(42, code);
    }

    @Test
    void run_showsMainWindow() {
        Application app = new Application();
        Window main = new Window();
        app.setMainWindow(main);
        app.run();
        assertTrue(main.getIsVisible());
        assertTrue(main.getIsLoaded());
    }

    @Test
    void run_passesArgumentsToStartup() {
        Application app = new Application();
        String[] got = new String[1];
        app.addStartupListener((s, e) -> got[0] = String.join(",", e.getArgs()));
        app.run(new String[]{"a", "b"});
        assertEquals("a,b", got[0]);
    }

    @Test
    void exitCode_settableFromExitEvent() {
        Application app = new Application();
        app.addExitListener((s, e) -> e.setExitCode(7));
        assertEquals(7, app.run());
    }

    @Test
    void windows_tracksOpenWindows() {
        Application app = new Application();
        Window a = new Window();
        Window b = new Window();
        a.show();
        b.show();
        WindowCollection windows = app.getWindows();
        assertEquals(2, windows.size());
        assertTrue(windows.contains(a));
        a.close();
        assertEquals(1, app.getWindows().size());
    }

    @Test
    void onLastWindowClose_shutsDownApp() {
        Application app = new Application();
        Window w = new Window();
        w.show();
        assertFalse(UiLoop.get().isShuttingDown());

        w.close();
        assertTrue(UiLoop.get().isShuttingDown());
    }

    @Test
    void onExplicitShutdown_doesNotShutdownOnClose() {
        Application app = new Application();
        app.setShutdownMode(ShutdownMode.OnExplicitShutdown);
        Window w = new Window();
        w.show();
        w.close();
        assertFalse(UiLoop.get().isShuttingDown());
    }

    @Test
    void onMainWindowClose_shutsDownOnlyForMainWindow() {
        Application app = new Application();
        app.setShutdownMode(ShutdownMode.OnMainWindowClose);
        Window main = new Window();
        Window other = new Window();
        app.setMainWindow(main);

        other.show();
        other.close();
        assertFalse(UiLoop.get().isShuttingDown());

        main.show();
        main.close();
        assertTrue(UiLoop.get().isShuttingDown());
    }

    @Test
    void activatedDeactivated_followActiveWindow() {
        Application app = new Application();
        AtomicInteger activated = new AtomicInteger();
        AtomicInteger deactivated = new AtomicInteger();
        app.addActivatedListener((s, e) -> activated.incrementAndGet());
        app.addDeactivatedListener((s, e) -> deactivated.incrementAndGet());

        Window a = new Window();
        Window b = new Window();
        a.show();
        assertEquals(1, activated.get());
        assertEquals(0, deactivated.get());

        b.show();
        assertEquals(1, activated.get(), "second window must not re-activate the app");

        b.close();
        assertEquals(0, deactivated.get(), "another active window keeps the app active");

        a.close();
        assertEquals(1, deactivated.get());
    }

    @Test
    void sessionEnding_cancelable() {
        Application app = new Application();
        AtomicInteger reason = new AtomicInteger(-1);
        app.addSessionEndingListener((s, e) -> {
            reason.set(e.getReasonSessionEnding().getValue());
            e.setCancel(true);
        });
        app.raiseSessionEnding(SessionEndingCancelEventArgs.ReasonSessionEnding.Logoff);
        assertEquals(0, reason.get());
    }

    @Test
    void sessionEnding_onHookInvoked() {
        final boolean[] hooked = {false};
        Application app = new Application() {
            @Override
            protected void onSessionEnding(SessionEndingCancelEventArgs e) {
                hooked[0] = true;
            }
        };
        app.raiseSessionEnding(SessionEndingCancelEventArgs.ReasonSessionEnding.SystemShutdown);
        assertTrue(hooked[0]);
    }

    @Test
    void dispatcherUnhandledException_routesThrowables() {
        Application app = new Application();
        Throwable[] got = new Throwable[1];
        app.addDispatcherUnhandledExceptionListener((s, e) -> got[0] = e);
        app.raiseDispatcherUnhandledException(new RuntimeException("boom"));
        assertEquals("boom", got[0].getMessage());
    }

    @Test
    void startupUri_stored() {
        Application app = new Application();
        app.setStartupUri("MainWindow.xaml");
        assertEquals("MainWindow.xaml", app.getStartupUri());
    }

    @Test
    void onStartupHook_called() {
        final boolean[] started = {false};
        Application app = new Application() {
            @Override
            protected void onStartup(StartupEventArgs e) {
                started[0] = true;
            }
        };
        app.run();
        assertTrue(started[0]);
    }
}