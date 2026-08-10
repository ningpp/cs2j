package dotnet.wpf.swing;

import dotnet.wpf.swing.controls.ContextMenu;
import dotnet.wpf.swing.controls.MenuItem;
import dotnet.wpf.swing.controls.ToolTip;
import org.junit.jupiter.api.Test;

import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.*;

class MenuToolTipTest {

    @Test
    void menuItem_headerAndClick() {
        MenuItem item = new MenuItem();
        item.setHeader("Zoom");
        assertEquals("Zoom", item.getHeader());
        AtomicInteger clicks = new AtomicInteger();
        item.addClickListener((s, e) -> clicks.incrementAndGet());
        item.raiseClick();
        assertEquals(1, clicks.get());
    }

    @Test
    void contextMenu_itemsAndClosed() {
        ContextMenu menu = new ContextMenu();
        MenuItem item = new MenuItem();
        menu.getItems().add(item);
        assertEquals(1, menu.getItems().size());
        AtomicInteger closed = new AtomicInteger();
        menu.addClosedListener((s, e) -> closed.incrementAndGet());
        menu.raiseClosed();
        assertEquals(1, closed.get());
    }

    @Test
    void toolTip_content() {
        ToolTip tip = new ToolTip();
        tip.setContent(new dotnet.wpf.swing.controls.TextBlock());
        assertNotNull(tip.getContent());
        tip.setIsOpen(true);
        assertTrue(tip.getIsOpen());
    }
}
