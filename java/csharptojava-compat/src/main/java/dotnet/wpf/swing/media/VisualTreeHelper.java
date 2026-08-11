package dotnet.wpf.swing.media;

import dotnet.wpf.swing.DpiScale;
import dotnet.wpf.swing.Rect;
import dotnet.wpf.swing.UIElement;

import java.util.List;
import java.util.function.Function;

/**
 * System.Windows.Media.VisualTreeHelper compatibility class.
 * Swing-backed hit testing walks the visual child tree and reports the front-most
 * element whose bounds contain the query geometry.
 */
public final class VisualTreeHelper {

    private VisualTreeHelper() {
    }

    public static DpiScale getDpi(Visual visual) {
        java.awt.Toolkit tk = java.awt.Toolkit.getDefaultToolkit();
        try {
            int dpi = tk.getScreenResolution();
            double scale = dpi / 96.0;
            return new DpiScale(scale, scale);
        } catch (Throwable t) {
            return new DpiScale();
        }
    }

    public static int getChildrenCount(Visual reference) {
        return reference == null ? 0 : reference.getVisualChildrenCount();
    }

    public static Visual getChild(Visual reference, int childIndex) {
        return reference == null ? null : reference.getVisualChild(childIndex);
    }

    public static Rect getContentBounds(Visual reference) {
        if (reference instanceof UIElement) {
            dotnet.wpf.swing.Size s = ((UIElement) reference).getRenderSize();
            return new Rect(0, 0, s.getWidth(), s.getHeight());
        }
        return Rect.EMPTY;
    }

    public static Rect getDescendantBounds(Visual reference) {
        return getContentBounds(reference);
    }

    public static void hitTest(Visual reference, Object filterCallback,
                               HitTestResultCallback resultCallback, HitTestParameters hitTestParameters) {
        Geometry hitGeometry = hitTestParameters instanceof GeometryHitTestParameters
                ? ((GeometryHitTestParameters) hitTestParameters).getHitGeometry()
                : null;
        if (hitGeometry == null || hitGeometry.isEmpty()) {
            return;
        }
        Rect query = hitGeometry.getBounds();
        if (reference == null) {
            return;
        }
        hitTestRecursive(reference, query, resultCallback);
    }

    public static HitTestResult hitTest(Visual reference, dotnet.wpf.swing.Point point) {
        final HitTestResult[] found = new HitTestResult[1];
        hitTestRecursive(reference, new Rect(point, new dotnet.wpf.swing.Size(0, 0)), result -> {
            found[0] = result;
            return HitTestResultBehavior.Stop;
        });
        return found[0];
    }

    private static HitTestResultBehavior hitTestRecursive(Visual visual, Rect query,
                                                          HitTestResultCallback callback) {
        if (visual == null) {
            return HitTestResultBehavior.Continue;
        }
        // Pre-order so that front-most (last child) wins; simple post-order here.
        for (int i = visual.getVisualChildrenCount() - 1; i >= 0; i--) {
            Visual child = visual.getVisualChild(i);
            HitTestResultBehavior childResult = hitTestRecursive(child, query, callback);
            if (childResult == HitTestResultBehavior.Stop) {
                return HitTestResultBehavior.Stop;
            }
        }
        if (intersects(visual, query)) {
            HitTestResult r = new SimpleHitTestResult(visual);
            HitTestResultBehavior b = callback != null ? callback.invoke(r) : HitTestResultBehavior.Continue;
            if (b == HitTestResultBehavior.Stop) {
                return HitTestResultBehavior.Stop;
            }
        }
        return HitTestResultBehavior.Continue;
    }

    private static boolean intersects(Visual visual, Rect query) {
        Rect bounds = getContentBounds(visual);
        return bounds != null && !bounds.isEmpty() && bounds.intersectsWith(query);
    }

    private static final class SimpleHitTestResult extends HitTestResult {
        private final Visual visual;

        SimpleHitTestResult(Visual visual) {
            this.visual = visual;
        }

        @Override
        public Object getVisualHit() {
            return visual;
        }
    }
}
