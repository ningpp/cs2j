package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.Rect;
import dotnet.wpf.swing.Size;
import dotnet.wpf.swing.UIElement;
import dotnet.wpf.swing.media.Brushes;
import dotnet.wpf.swing.shapes.Rectangle;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class CanvasPanelTest {

    @Test
    void children_addRemove() {
        Canvas canvas = new Canvas();
        Rectangle r = new Rectangle();
        assertEquals(0, canvas.getChildrenCount());
        canvas.addChild(r);
        assertEquals(1, canvas.getChildrenCount());
        assertEquals(1, canvas.getVisualChildrenCount());
        canvas.removeChild(r);
        assertEquals(0, canvas.getChildrenCount());
    }

    @Test
    void childrenCollection_addSetsVisualParent() {
        Canvas canvas = new Canvas();
        Rectangle r = new Rectangle();

        canvas.getChildren().add(r);

        assertTrue(canvas.getChildren().contains(r));
        assertEquals(1, canvas.getVisualChildrenCount());
        assertSame(canvas, r.getParent(), "Children.Add must register the visual parent like WPF");
    }

    @Test
    void childrenCollection_removeClearsVisualParent() {
        Canvas canvas = new Canvas();
        Rectangle r = new Rectangle();
        canvas.getChildren().add(r);

        canvas.getChildren().remove(r);

        assertEquals(0, canvas.getChildrenCount());
        assertEquals(0, canvas.getVisualChildrenCount());
        assertNull(r.getParent());
    }

    @Test
    void clearChildren() {
        Canvas canvas = new Canvas();
        canvas.addChild(new Rectangle());
        canvas.addChild(new Rectangle());
        canvas.clearChildren();
        assertEquals(0, canvas.getChildrenCount());
    }

    @Test
    void attachedLeftTop() {
        Rectangle r = new Rectangle();
        Canvas.setLeft(r, 10);
        Canvas.setTop(r, 20);
        assertEquals(10.0, Canvas.getLeft(r));
        assertEquals(20.0, Canvas.getTop(r));
    }

    @Test
    void background() {
        Canvas canvas = new Canvas();
        assertNull(canvas.getBackground());
        canvas.setBackground(Brushes.White);
        assertEquals(Brushes.White, canvas.getBackground());
    }

    @Test
    void panelBackground_andZIndex() {
        Canvas canvas = new Canvas();
        Rectangle r = new Rectangle();
        canvas.setBackground(Brushes.Red);
        Panel.setZIndex(r, 5);
        assertEquals(5, Panel.getZIndex(r));
    }
}
