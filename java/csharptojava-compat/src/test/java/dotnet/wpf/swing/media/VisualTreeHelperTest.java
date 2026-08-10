package dotnet.wpf.swing.media;

import dotnet.wpf.swing.DpiScale;
import dotnet.wpf.swing.UIElement;
import dotnet.wpf.swing.controls.Canvas;
import dotnet.wpf.swing.shapes.Rectangle;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class VisualTreeHelperTest {

    @Test
    void childrenCountAndChild() {
        Canvas canvas = new Canvas();
        Rectangle r1 = new Rectangle();
        Rectangle r2 = new Rectangle();
        canvas.addChild(r1);
        canvas.addChild(r2);
        assertEquals(2, VisualTreeHelper.getChildrenCount(canvas));
        assertSame(r1, VisualTreeHelper.getChild(canvas, 0));
    }

    @Test
    void hitTest_geometryFindsFrontMost() {
        Canvas canvas = new Canvas();
        Rectangle r = new Rectangle();
        r.setWidth(100);
        r.setHeight(100);
        r.arrange(new dotnet.wpf.swing.Rect(0, 0, 100, 100));
        canvas.addChild(r);

        final Object[] hit = new Object[1];
        GeometryHitTestParameters params = new GeometryHitTestParameters(
                new RectangleGeometry(new dotnet.wpf.swing.Rect(50, 50, 10, 10)));
        VisualTreeHelper.hitTest(canvas, null, result -> {
            hit[0] = result.getVisualHit();
            return HitTestResultBehavior.Stop;
        }, params);
        assertNotNull(hit[0]);
        assertSame(r, hit[0]);
    }

    @Test
    void getDpi_returnsScale() {
        DpiScale dpi = VisualTreeHelper.getDpi(canvas());
        assertTrue(dpi.getDpiScaleX() > 0);
        assertTrue(dpi.getPixelsPerDip() > 0);
    }

    private static Visual canvas() {
        return new Canvas();
    }
}
