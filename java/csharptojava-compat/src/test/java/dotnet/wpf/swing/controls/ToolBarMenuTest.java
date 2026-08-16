package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.Size;
import dotnet.wpf.swing.input.CommandBinding;
import dotnet.wpf.swing.input.CommandBindingCollection;
import dotnet.wpf.swing.input.ExecutedRoutedEventHandler;
import dotnet.wpf.swing.input.RoutedUICommand;
import org.junit.jupiter.api.Test;

import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.*;

class ToolBarMenuTest {

    @Test
    void menuItem_measureReturnsVisibleSize() {
        MenuItem item = new MenuItem();
        item.setHeader("Open");

        item.measure(new Size(Double.POSITIVE_INFINITY, Double.POSITIVE_INFINITY));

        assertTrue(item.getDesiredSize().getWidth() > 0);
        assertTrue(item.getDesiredSize().getHeight() > 0);
    }

    @Test
    void toolbar_measureReturnsHorizontalHeight() {
        ToolBar toolbar = new ToolBar();
        MenuItem item = new MenuItem();
        item.setHeader("File");
        toolbar.addChild(item);

        toolbar.measure(new Size(Double.POSITIVE_INFINITY, Double.POSITIVE_INFINITY));

        assertTrue(toolbar.getDesiredSize().getWidth() >= item.getDesiredSize().getWidth());
        assertTrue(toolbar.getDesiredSize().getHeight() > 0);
    }

    @Test
    void menu_measureReturnsSumOfItemWidths() {
        Menu menu = new Menu();
        MenuItem file = new MenuItem();
        file.setHeader("File");
        MenuItem view = new MenuItem();
        view.setHeader("View");
        menu.addChild(file);
        menu.addChild(view);

        menu.measure(new Size(Double.POSITIVE_INFINITY, Double.POSITIVE_INFINITY));

        assertTrue(menu.getDesiredSize().getWidth() >=
                file.getDesiredSize().getWidth() + view.getDesiredSize().getWidth());
    }

    @Test
    void commandBindingCollection_findsCommand() {
        RoutedUICommand command = new RoutedUICommand("Open", "OpenFileCommand", ToolBarMenuTest.class);
        AtomicInteger executed = new AtomicInteger();
        ExecutedRoutedEventHandler handler = (s, e) -> executed.incrementAndGet();
        CommandBindingCollection bindings = new CommandBindingCollection();
        bindings.add(new CommandBinding(command, handler));

        CommandBinding found = bindings.find(command);

        assertNotNull(found);
        found.getExecuted().accept(this, new dotnet.wpf.swing.input.ExecutedRoutedEventArgs());
        assertEquals(1, executed.get());
    }
}
