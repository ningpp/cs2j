package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.UIElement;

import java.util.IdentityHashMap;

/**
 * System.Windows.Controls.Canvas compatibility class.
 */
public class Canvas extends Panel {

    private static final IdentityHashMap<UIElement, Double> LEFT = new IdentityHashMap<>();
    private static final IdentityHashMap<UIElement, Double> TOP = new IdentityHashMap<>();

    public static void setLeft(UIElement element, double length) {
        LEFT.put(element, length);
    }

    public static double getLeft(UIElement element) {
        Double v = element == null ? null : LEFT.get(element);
        return v == null ? Double.NaN : v;
    }

    public static void setTop(UIElement element, double length) {
        TOP.put(element, length);
    }

    public static double getTop(UIElement element) {
        Double v = element == null ? null : TOP.get(element);
        return v == null ? Double.NaN : v;
    }

    public static void setRight(UIElement element, double length) {
        // right anchoring unsupported in the simple Swing-backed model
    }

    public static void setBottom(UIElement element, double length) {
        // bottom anchoring unsupported
    }
}
