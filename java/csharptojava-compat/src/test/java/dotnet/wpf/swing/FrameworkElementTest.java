package dotnet.wpf.swing;

import dotnet.wpf.swing.controls.Canvas;
import dotnet.wpf.swing.controls.ContextMenuService;
import dotnet.wpf.swing.controls.TextBlock;
import dotnet.wpf.swing.controls.ToolTipService;
import dotnet.wpf.swing.shapes.Rectangle;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class FrameworkElementTest {

    @Test
    void widthHeight_andActual() {
        Rectangle r = new Rectangle();
        r.setWidth(100);
        r.setHeight(50);
        assertEquals(100.0, r.getWidth());
        assertEquals(50.0, r.getHeight());
        assertEquals(100.0, r.getActualWidth());
        assertEquals(50.0, r.getActualHeight());
    }

    @Test
    void tag_toolTip_name() {
        Rectangle r = new Rectangle();
        r.setTag("myTag");
        assertEquals("myTag", r.getTag());
        r.setToolTip("tooltip");
        assertEquals("tooltip", r.getToolTip());
        r.setName("rect1");
        assertEquals("rect1", r.getName());
    }

    @Test
    void minMaxBounds() {
        Rectangle r = new Rectangle();
        r.setMinWidth(10);
        r.setMaxWidth(200);
        r.setMinHeight(5);
        r.setMaxHeight(100);
        assertEquals(10.0, r.getMinWidth());
        assertEquals(200.0, r.getMaxWidth());
        assertEquals(5.0, r.getMinHeight());
        assertEquals(100.0, r.getMaxHeight());
    }

    @Test
    void textBlock_measure() {
        TextBlock tb = new TextBlock();
        tb.setText("Hello");
        tb.setFontSize(12);
        tb.measure(new Size(Double.POSITIVE_INFINITY, Double.POSITIVE_INFINITY));
        assertTrue(tb.getDesiredSize().getWidth() > 0);
        assertTrue(tb.getDesiredSize().getHeight() > 0);
    }
}
