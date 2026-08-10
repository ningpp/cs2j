package dotnet.wpf.swing;

import dotnet.wpf.swing.controls.Canvas;
import dotnet.wpf.swing.input.MouseButtonEventArgs;
import dotnet.wpf.swing.shapes.Path;
import org.junit.jupiter.api.Test;

import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.*;

class UIElementTest {

    @Test
    void visibility_setting() {
        UIElement e = new Canvas();
        assertEquals(Visibility.Visible, e.getVisibility());
        assertTrue(e.getIsVisible());
        e.setVisibility(Visibility.Hidden);
        assertEquals(Visibility.Hidden, e.getVisibility());
        assertFalse(e.getIsVisible());
    }

    @Test
    void hitTestVisible_andFocusable() {
        UIElement e = new Canvas();
        assertTrue(e.getIsHitTestVisible());
        e.setIsHitTestVisible(false);
        assertFalse(e.getIsHitTestVisible());
        e.setFocusable(true);
        assertTrue(e.getFocusable());
    }

    @Test
    void measure_setsDesiredSize() {
        UIElement e = new Canvas();
        e.measure(new Size(100, 50));
        assertEquals(new Size(100, 50), e.getDesiredSize());
    }

    @Test
    void arrange_setsRenderSize() {
        UIElement e = new Canvas();
        e.arrange(new Rect(0, 0, 320, 240));
        assertEquals(new Size(320, 240), e.getRenderSize());
    }

    @Test
    void mouseLeftButtonDown_listener_fires() {
        Canvas c = new Canvas();
        AtomicInteger count = new AtomicInteger();
        c.addMouseLeftButtonDownListener((sender, args) -> count.incrementAndGet());
        c.raiseMouseLeftButtonDown(new MouseButtonEventArgs());
        assertEquals(1, count.get());
    }

    @Test
    void removeMouseLeftButtonDown_listener_unsubscribes() {
        Canvas c = new Canvas();
        AtomicInteger count = new AtomicInteger();
        java.util.function.BiConsumer<Object, MouseButtonEventArgs> handler =
                (s, a) -> count.incrementAndGet();
        c.addMouseLeftButtonDownListener(handler);
        c.removeMouseLeftButtonDownListener(handler);
        c.raiseMouseLeftButtonDown(new MouseButtonEventArgs());
        assertEquals(0, count.get());
    }

    @Test
    void panelZIndex_attachedProperty() {
        Path p = new Path();
        assertEquals(0, dotnet.wpf.swing.controls.Panel.getZIndex(p));
        dotnet.wpf.swing.controls.Panel.setZIndex(p, 42);
        assertEquals(42, dotnet.wpf.swing.controls.Panel.getZIndex(p));
    }
}
