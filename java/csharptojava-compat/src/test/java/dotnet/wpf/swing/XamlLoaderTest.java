package dotnet.wpf.swing;

import dotnet.wpf.swing.controls.DockPanel;
import dotnet.wpf.swing.controls.Menu;
import dotnet.wpf.swing.controls.MenuItem;
import dotnet.wpf.swing.controls.StatusBar;
import dotnet.wpf.swing.controls.TextBox;
import dotnet.wpf.swing.controls.ToolBar;
import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for {@link Application#loadComponent(Window, Object)}.
 *
 * <p>The compat layer must not leave generated XAML partial-class fields
 * null. When a classpath XAML resource is available it should be parsed and
 * connected; when it is missing (converted projects that do not copy XAML
 * resources) a usable default DockPanel layout must still be produced.</p>
 */
class XamlLoaderTest {

    static class MissingResourceWindow extends Window implements IComponentConnector {
        public DockPanel dockPanel;
        public TextBox _searchBox;
        public TextBox statusTextBox;
        public DockPanel graphViewerPanel;
        public final List<Integer> connected = new ArrayList<>();

        @Override
        public void initializeComponent() {
        }

        @Override
        public void connect(int connectionId, Object target) {
            connected.add(connectionId);
            switch (connectionId) {
                case 1:
                    this.dockPanel = (DockPanel) target;
                    break;
                case 2:
                    this._searchBox = (TextBox) target;
                    break;
                case 3:
                    this.statusTextBox = (TextBox) target;
                    break;
                case 4:
                    this.graphViewerPanel = (DockPanel) target;
                    break;
                default:
                    break;
            }
        }
    }

    static class ResourceWindow extends Window implements IComponentConnector {
        public DockPanel dockPanel;
        public TextBox _searchBox;
        public DockPanel graphViewerPanel;
        public final List<Integer> connected = new ArrayList<>();

        @Override
        public void initializeComponent() {
        }

        @Override
        public void connect(int connectionId, Object target) {
            connected.add(connectionId);
            switch (connectionId) {
                case 1:
                    this.dockPanel = (DockPanel) target;
                    break;
                case 2:
                    this._searchBox = (TextBox) target;
                    break;
                case 3:
                    this.graphViewerPanel = (DockPanel) target;
                    break;
                default:
                    break;
            }
        }
    }

    static class ToolWindow extends Window implements IComponentConnector {
        public DockPanel dockPanel;
        public TextBox _searchBox;
        public TextBox statusTextBox;
        public DockPanel graphViewerPanel;

        @Override
        public void initializeComponent() {
        }

        @Override
        public void connect(int connectionId, Object target) {
            switch (connectionId) {
                case 1:
                    this.dockPanel = (DockPanel) target;
                    break;
                case 2:
                    this._searchBox = (TextBox) target;
                    break;
                case 3:
                    this.statusTextBox = (TextBox) target;
                    break;
                case 4:
                    this.graphViewerPanel = (DockPanel) target;
                    break;
                default:
                    break;
            }
        }
    }

    @Test
    void loadComponent_missingResource_buildsDefaultLayoutAndConnects() {
        MissingResourceWindow w = new MissingResourceWindow();

        Application.loadComponent(w, "missing.xaml");

        assertNotNull(w.dockPanel, "dockPanel must not be null after loadComponent");
        assertNotNull(w._searchBox, "_searchBox must not be null after loadComponent");
        assertNotNull(w.statusTextBox, "statusTextBox must not be null after loadComponent");
        assertNotNull(w.graphViewerPanel, "graphViewerPanel must not be null after loadComponent");
        assertSame(w.dockPanel, w.getContent(), "root DockPanel must be installed as window content");
        assertTrue(w.dockPanel.getChildren().contains(w._searchBox));
        assertTrue(w.dockPanel.getChildren().contains(w.statusTextBox));
        assertTrue(w.dockPanel.getChildren().contains(w.graphViewerPanel));
        assertEquals(List.of(1, 2, 3, 4), w.connected, "default layout must connect generated fields in order");
    }

    @Test
    void loadComponent_classpathXaml_parsesConnectsAndSetsContent() {
        ResourceWindow w = new ResourceWindow();

        Application.loadComponent(w, "/Test;component/dotnet/wpf/swing/TestWindow.xaml");

        assertNotNull(w.dockPanel, "dockPanel must be connected from XAML");
        assertNotNull(w._searchBox, "_searchBox must be connected from XAML");
        assertNotNull(w.graphViewerPanel, "graphViewerPanel must be connected from XAML");
        assertSame(w.dockPanel, w.getContent(), "XAML root panel must be installed as window content");
        assertEquals("dockPanel", w.dockPanel.getName());
        assertEquals("_searchBox", w._searchBox.getName());
        assertEquals("graphViewerPanel", w.graphViewerPanel.getName());
        assertEquals("Find", w._searchBox.getText());
        assertEquals(156.0, w._searchBox.getWidth());
        assertEquals(23.0, w._searchBox.getHeight());
        assertTrue(w.dockPanel.getChildren().contains(w._searchBox));
        assertTrue(w.dockPanel.getChildren().contains(w.graphViewerPanel));
        assertEquals(List.of(1, 2, 3), w.connected, "XAML named elements must be connected in document order");
    }

    @Test
    void loadComponent_toolbarMenuStatusBar_areBuiltAndConnected() {
        ToolWindow w = new ToolWindow();

        Application.loadComponent(w, "/Test;component/dotnet/wpf/swing/ToolWindow.xaml");

        assertNotNull(w.dockPanel);
        assertNotNull(w._searchBox);
        assertNotNull(w.statusTextBox);
        assertNotNull(w.graphViewerPanel);
        assertSame(w.dockPanel, w.getContent());

        ToolBar toolbar = w.dockPanel.getChildren().stream()
                .filter(ToolBar.class::isInstance)
                .map(ToolBar.class::cast)
                .findFirst().orElse(null);
        assertNotNull(toolbar, "ToolBar must be parsed from XAML");
        Menu menu = toolbar.getChildren().stream()
                .filter(Menu.class::isInstance)
                .map(Menu.class::cast)
                .findFirst().orElse(null);
        assertNotNull(menu, "Menu must be parsed inside ToolBar");
        MenuItem file = menu.getChildren().stream()
                .filter(MenuItem.class::isInstance)
                .map(MenuItem.class::cast)
                .findFirst().orElse(null);
        assertNotNull(file, "top-level MenuItem must be parsed");
        assertTrue(file.getItems().getCount() > 0);
        MenuItem open = (MenuItem) file.getItems().getItems().get(0);
        assertSame(TestCommands.OpenCommand, open.getCommand(), "Command attribute must resolve to the static command");

        StatusBar statusBar = w.dockPanel.getChildren().stream()
                .filter(StatusBar.class::isInstance)
                .map(StatusBar.class::cast)
                .findFirst().orElse(null);
        assertNotNull(statusBar, "StatusBar must be parsed from XAML");
        assertTrue(statusBar.getChildren().contains(w.statusTextBox));
    }
}
