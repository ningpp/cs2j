package dotnet.wpf.swing;

import dotnet.wpf.swing.controls.Canvas;
import dotnet.wpf.swing.controls.DockPanel;
import dotnet.wpf.swing.controls.TextBlock;
import dotnet.wpf.swing.internal.UiLoop;
import dotnet.wpf.swing.shapes.Rectangle;
import io.github.ningpp.compat.CancelEventArgs;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Headless window lifecycle tests: event order, cancelable closing,
 * state/location changes and modal dialogs.
 */
class WindowTest {

    @BeforeEach
    void cleanUpLeakedWindows() {
        Window.closeAll();
        UiLoop.get().reset();
    }

    @AfterEach
    void resetLoop() {
        Window.closeAll();
        UiLoop.get().reset();
    }

    @Test
    void show_raisesLifecycleInOrder() {
        Window w = new Window();
        List<String> events = new ArrayList<>();
        w.addSourceInitializedListener((s, e) -> events.add("SourceInitialized"));
        w.addClosedListener((s, e) -> events.add("Closed"));
        w.addContentRenderedListener((s, e) -> events.add("ContentRendered"));
        w.addActivatedListener((s, e) -> events.add("Activated"));
        w.addDeactivatedListener((s, e) -> events.add("Deactivated"));
        w.addLoadedListener((s, e) -> events.add("Loaded"));
        w.addUnloadedListener((s, e) -> events.add("Unloaded"));
        w.addClosingListener((s, e) -> events.add("Closing"));

        w.show();
        assertTrue(w.getIsVisible());
        assertTrue(w.getIsActive());
        assertTrue(w.getIsLoaded());
        assertEquals(List.of("SourceInitialized", "Loaded", "ContentRendered", "Activated"), events);

        w.close();
        assertFalse(w.getIsVisible());
        assertFalse(w.getIsActive());
        assertFalse(w.getIsLoaded());
        assertEquals(List.of("SourceInitialized", "Loaded", "ContentRendered", "Activated",
                "Closing", "Unloaded", "Deactivated", "Closed"), events);
    }

    @Test
    void show_loadsContentTree() {
        Window w = new Window();
        DockPanel panel = new DockPanel();
        Rectangle r = new Rectangle();
        panel.addChild(r);
        w.setContent(panel);

        w.show();
        assertTrue(w.getIsLoaded());
        assertTrue(panel.getIsLoaded());
        assertTrue(r.getIsLoaded());

        w.close();
        assertFalse(panel.getIsLoaded());
        assertFalse(r.getIsLoaded());
    }

    @Test
    void closing_isCancelable() {
        Window w = new Window();
        BiConsumer<Object, CancelEventArgs> canceler = (s, e) -> e.setCancel(true);
        w.addClosingListener(canceler);
        w.show();

        w.close();
        assertTrue(w.getIsVisible(), "canceled close must keep the window visible");
        assertTrue(w.getIsLoaded());
        assertTrue(w.getIsActive());
        assertTrue(Window.getOpenWindowCount() >= 1);

        w.removeClosingListener(canceler);
        w.close();
        assertFalse(w.getIsVisible());
    }

    @Test
    void hide_unloadsWithoutClosing() {
        Window w = new Window();
        List<String> events = new ArrayList<>();
        w.addClosedListener((s, e) -> events.add("Closed"));
        w.addUnloadedListener((s, e) -> events.add("Unloaded"));
        w.addDeactivatedListener((s, e) -> events.add("Deactivated"));
        w.show();
        events.clear();

        w.hide();
        assertEquals(List.of("Unloaded", "Deactivated"), events);
        assertFalse(w.getIsVisible());

        w.show();
        assertTrue(w.getIsVisible());
        assertTrue(w.getIsLoaded());
    }

    @Test
    void windowState_raisesStateChanged() {
        Window w = new Window();
        List<WindowState> states = new ArrayList<>();
        w.addStateChangedListener((s, e) -> states.add(w.getWindowState()));
        w.show();

        w.setWindowState(WindowState.Minimized);
        w.setWindowState(WindowState.Maximized);
        w.setWindowState(WindowState.Normal);
        assertEquals(List.of(WindowState.Minimized, WindowState.Maximized, WindowState.Normal), states);
    }

    @Test
    void leftTop_raiseLocationChanged() {
        Window w = new Window();
        int[] count = {0};
        w.addLocationChangedListener((s, e) -> count[0]++);
        w.show();

        w.setLeft(100);
        w.setTop(50);
        assertEquals(100.0, w.getLeft());
        assertEquals(50.0, w.getTop());
        assertEquals(2, count[0]);
    }

    @Test
    void showDialog_returnsDialogResult() {
        Window a = new Window();
        a.setDialogResult(true);
        assertEquals(Boolean.TRUE, a.showDialog());
        assertTrue(a.getIsVisible());
        a.close();

        Window b = new Window();
        b.setDialogResult(false);
        assertEquals(Boolean.FALSE, b.showDialog());
    }

    @Test
    void styleAndOwnerProperties() {
        Window owner = new Window();
        Window w = new Window();
        w.setOwner(owner);
        w.setShowInTaskbar(false);
        w.setWindowStyle(WindowStyle.ToolWindow);
        w.setTopmost(true);
        w.setShowActivated(false);

        assertEquals(owner, w.getOwner());
        assertFalse(w.getShowInTaskbar());
        assertEquals(WindowStyle.ToolWindow, w.getWindowStyle());
        assertTrue(w.getTopmost());
        assertFalse(w.getShowActivated());
        assertEquals(WindowStyle.SingleBorderWindow, new Window().getWindowStyle());
    }

    @Test
    void windowCollection_snapshot() {
        Window a = new Window();
        Window b = new Window();
        a.show();
        b.show();
        WindowCollection windows = new WindowCollection(Window.getOpenWindows());
        assertEquals(2, windows.size());
        assertTrue(windows.contains(a));
        assertTrue(windows.contains(b));

        a.close();
        b.close();
        assertEquals(0, new WindowCollection(Window.getOpenWindows()).size());
    }

    @Test
    void contentRendered_manuallyRaisable() {
        Window w = new Window();
        int[] count = {0};
        w.addContentRenderedListener((s, e) -> count[0]++);
        w.raiseContentRendered();
        assertEquals(1, count[0]);
    }

    @Test
    void closingListener_usesCancelEventArgs() {
        Window w = new Window();
        BiConsumer<Object, CancelEventArgs> canceler = (s, e) -> e.setCancel(true);
        w.addClosingListener(canceler);
        w.show();
        w.close();
        assertTrue(w.getIsVisible());
        w.removeClosingListener(canceler);
        w.close();
        assertFalse(w.getIsVisible());
    }
}