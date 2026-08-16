package dotnet.wpf.swing;

import dotnet.wpf.swing.internal.SystemDpi;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * AWT-backed replacement for the GDI/User32 DPI P/Invoke stubs used by
 * generated GraphmapsWpfControl code.
 */
class SystemDpiTest {

    @Test
    void deviceCaps_logPixelsReturnsPositiveDpi() {
        assertTrue(SystemDpi.getDeviceCaps(0, 88) > 0, "LOGPIXELSX must be positive");
        assertTrue(SystemDpi.getDeviceCaps(0, 90) > 0, "LOGPIXELSY must be positive");
    }

    @Test
    void headless_logPixelsFallbackTo96() {
        if (java.awt.GraphicsEnvironment.isHeadless()) {
            assertEquals(96, SystemDpi.getDeviceCaps(0, 88));
            assertEquals(96, SystemDpi.getDeviceCaps(0, 90));
        }
    }

    @Test
    void windowHandles_areOpaqueAndDoNotThrow() {
        assertDoesNotThrow(() -> {
            int desktop = SystemDpi.getDesktopWindow();
            int dc = SystemDpi.getWindowDC(desktop);
            assertTrue(SystemDpi.releaseDC(desktop, dc) != 0);
        });
    }
}
