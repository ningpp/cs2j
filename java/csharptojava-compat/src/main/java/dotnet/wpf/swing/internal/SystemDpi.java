package dotnet.wpf.swing.internal;

/**
 * AWT-backed replacement for the GDI/User32 DPI P/Invoke stubs used by
 * generated GraphmapsWpfControl code.
 *
 * <p>The generated {@code NativeMethods} class delegates
 * {@code GetDeviceCaps}/{@code GetDesktopWindow}/{@code GetWindowDC}/
 * {@code ReleaseDC} here. Handles are opaque (Java2D owns the rendering
 * surface), so the only real value is the screen DPI reported by
 * {@code Toolkit.getScreenResolution()}.</p>
 */
public final class SystemDpi {

    private static final int LOGPIXELSX = 88;
    private static final int LOGPIXELSY = 90;
    private static final int FALLBACK_DPI = 96;

    private SystemDpi() {
    }

    /** Opaque desktop-window handle; Java2D has no native HWND. */
    public static int getDesktopWindow() {
        return 0;
    }

    /** Opaque device-context handle; rendering is handled by Java2D. */
    public static int getWindowDC(int hWnd) {
        return 0;
    }

    /** Releases a device context; always succeeds for the opaque handles. */
    public static int releaseDC(int hWnd, int hDc) {
        return 1;
    }

    /**
     * Returns the logical pixels-per-inch for the display. LOGPIXELSX/Y are
     * both mapped to AWT's screen resolution; unknown indexes fall back to 96.
     */
    public static int getDeviceCaps(int hdc, int nIndex) {
        if (nIndex != LOGPIXELSX && nIndex != LOGPIXELSY) {
            return FALLBACK_DPI;
        }
        try {
            if (java.awt.GraphicsEnvironment.isHeadless()) {
                return FALLBACK_DPI;
            }
            return java.awt.Toolkit.getDefaultToolkit().getScreenResolution();
        } catch (RuntimeException e) {
            return FALLBACK_DPI;
        }
    }
}
