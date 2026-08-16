package dotnet.winforms.swing;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

import io.github.ningpp.compat.EventArgs;
import java.util.function.BiConsumer;
import org.junit.jupiter.api.Test;

/**
 * Regression for the AGL conversion: FormStuff.java uses MenuStrip,
 * Form.getMainMenuStrip/setMainMenuStrip/addLoadListener, and
 * ToolStripMenuItem.getDropDownItems/setShortcutKeys, none of which existed in
 * the WinForms compatibility surface.
 */
class WinFormsMenuApiTest {

    @Test
    void menuStripAndMenuItemApisAreAvailable() {
        Form form = new Form();
        MenuStrip menu = new MenuStrip();

        form.setMainMenuStrip(menu);
        assertSame(menu, form.getMainMenuStrip());

        BiConsumer<Object, EventArgs> loadHandler = (sender, e) -> {
        };
        form.addLoadListener(loadHandler);

        ToolStripMenuItem fileItem = new ToolStripMenuItem("File");
        fileItem.getDropDownItems().add(new ToolStripMenuItem("Open file"));
        fileItem.setShortcutKeys(Keys.F5);

        assertEquals(1, fileItem.getDropDownItems().getCount());
        assertEquals(Keys.F5, fileItem.getShortcutKeys());
    }
}
