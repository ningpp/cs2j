package dotnet.wpf.swing.internal;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.HorizontalAlignment;
import dotnet.wpf.swing.IComponentConnector;
import dotnet.wpf.swing.UIElement;
import dotnet.wpf.swing.VerticalAlignment;
import dotnet.wpf.swing.Window;
import dotnet.wpf.swing.WindowStartupLocation;
import dotnet.wpf.swing.WindowState;
import dotnet.wpf.swing.controls.Border;
import dotnet.wpf.swing.controls.Canvas;
import dotnet.wpf.swing.controls.DockPanel;
import dotnet.wpf.swing.controls.Image;
import dotnet.wpf.swing.controls.Menu;
import dotnet.wpf.swing.controls.MenuItem;
import dotnet.wpf.swing.controls.Panel;
import dotnet.wpf.swing.controls.StatusBar;
import dotnet.wpf.swing.controls.TextBlock;
import dotnet.wpf.swing.controls.TextBox;
import dotnet.wpf.swing.controls.ToolBar;
import dotnet.wpf.swing.controls.Viewbox;
import dotnet.wpf.swing.media.Brush;
import dotnet.wpf.swing.media.BrushConverter;
import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;
import org.w3c.dom.NodeList;

import javax.xml.parsers.DocumentBuilderFactory;
import java.io.InputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Minimal XAML loader for the Swing-backed WPF compat layer.
 *
 * <p>Generated code calls {@code Application.loadComponent(window, uri)} from
 * {@code InitializeComponent}. This loader resolves pack URIs to classpath
 * resources, builds the small set of controls implemented by the compat
 * layer, and invokes {@link IComponentConnector#connect(int, Object)} for
 * named elements in document order. When the XAML resource is absent (for
 * example converted projects that do not copy {@code .xaml} resources), it
 * falls back to a default DockPanel layout so generated XAML partial-class
 * fields are never left null.</p>
 */
public final class XamlLoader {

    private static final String XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml";

    private XamlLoader() {
    }

    public static void loadComponent(Window window, Object resourceLocator) {
        if (window == null) {
            return;
        }
        InputStream in = openResource(window, resourceLocator);
        if (in != null) {
            try {
                parse(window, in);
                if (window.getContent() != null) {
                    return;
                }
            } catch (Exception ignored) {
                // A broken/unsupported XAML resource must not leave generated
                // fields null; the default layout below is the safe fallback.
            } finally {
                try {
                    in.close();
                } catch (Exception ignored) {
                    // best effort
                }
            }
        }
        buildDefaultLayout(window);
    }

    // ------------------------------------------------------------------
    // resource resolution
    // ------------------------------------------------------------------

    private static InputStream openResource(Window window, Object resourceLocator) {
        String path = resourcePath(resourceLocator);
        if (path == null) {
            return null;
        }
        ClassLoader loader = window.getClass().getClassLoader();
        InputStream in = loader == null ? null : loader.getResourceAsStream(path);
        if (in == null) {
            ClassLoader context = Thread.currentThread().getContextClassLoader();
            if (context != null) {
                in = context.getResourceAsStream(path);
            }
        }
        return in;
    }

    private static String resourcePath(Object resourceLocator) {
        if (resourceLocator == null) {
            return null;
        }
        String s = String.valueOf(resourceLocator).trim();
        int marker = s.indexOf("component/");
        if (marker >= 0) {
            s = s.substring(marker + "component/".length());
        }
        s = s.replace('\\', '/');
        while (s.startsWith("/")) {
            s = s.substring(1);
        }
        return s.isEmpty() ? null : s;
    }

    // ------------------------------------------------------------------
    // XAML parsing
    // ------------------------------------------------------------------

    private static void parse(Window window, InputStream in) throws Exception {
        DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
        factory.setNamespaceAware(true);
        Document document = factory.newDocumentBuilder().parse(in);
        Element root = document.getDocumentElement();
        IdCounter ids = new IdCounter();
        if ("Window".equals(localName(root))) {
            applyWindowAttributes(window, root);
            UIElement content = firstContentElement(window, root, ids);
            if (content != null) {
                window.setContent(content);
            }
        } else {
            UIElement content = buildElement(root, null, window, ids, null);
            if (content != null) {
                window.setContent(content);
            }
        }
    }

    private static UIElement firstContentElement(Window window, Element root, IdCounter ids) {
        NodeList children = root.getChildNodes();
        for (int i = 0; i < children.getLength(); i++) {
            Node node = children.item(i);
            if (node.getNodeType() != Node.ELEMENT_NODE) {
                continue;
            }
            Element child = (Element) node;
            String tag = localName(child);
            if ("Window.InputBindings".equals(tag) || "Window.Resources".equals(tag)) {
                continue;
            }
            return buildElement(child, null, window, ids, null);
        }
        return null;
    }

    private static UIElement buildElement(Element el, Panel parent, Window window,
                                          IdCounter ids, String inheritedDock) {
        String tag = localName(el);
        if ("DockPanel".equals(tag) || "Canvas".equals(tag)
                || "ToolBar".equals(tag) || "StatusBar".equals(tag) || "Menu".equals(tag)) {
            Panel panel = (Panel) createElement(tag);
            applyProperties(panel, el);
            connectIfNamed(panel, el, window, ids);
            NodeList children = el.getChildNodes();
            for (int i = 0; i < children.getLength(); i++) {
                Node node = children.item(i);
                if (node.getNodeType() == Node.ELEMENT_NODE) {
                    buildElement((Element) node, panel, window, ids,
                            inheritedDock != null ? inheritedDock : dockValue((Element) node));
                }
            }
            addToParent(parent, panel, inheritedDock != null ? inheritedDock : dockValue(el));
            return panel;
        }
        if ("Border".equals(tag)) {
            Border border = (Border) createElement(tag);
            applyProperties(border, el);
            connectIfNamed(border, el, window, ids);
            NodeList children = el.getChildNodes();
            for (int i = 0; i < children.getLength(); i++) {
                Node node = children.item(i);
                if (node.getNodeType() == Node.ELEMENT_NODE) {
                    UIElement child = buildElement((Element) node, null, window, ids, null);
                    if (child != null) {
                        border.setChild(child);
                    }
                    break;
                }
            }
            addToParent(parent, border, inheritedDock != null ? inheritedDock : dockValue(el));
            return border;
        }
        if ("Viewbox".equals(tag)) {
            Viewbox viewbox = (Viewbox) createElement(tag);
            applyProperties(viewbox, el);
            connectIfNamed(viewbox, el, window, ids);
            NodeList children = el.getChildNodes();
            for (int i = 0; i < children.getLength(); i++) {
                Node node = children.item(i);
                if (node.getNodeType() == Node.ELEMENT_NODE) {
                    UIElement child = buildElement((Element) node, null, window, ids, null);
                    if (child != null) {
                        viewbox.setChild(child);
                    }
                    break;
                }
            }
            addToParent(parent, viewbox, inheritedDock != null ? inheritedDock : dockValue(el));
            return viewbox;
        }

        if ("MenuItem".equals(tag)) {
            MenuItem menuItem = (MenuItem) createElement(tag);
            applyProperties(menuItem, el);
            applyCommand(menuItem, el, window);
            connectIfNamed(menuItem, el, window, ids);
            NodeList children = el.getChildNodes();
            for (int i = 0; i < children.getLength(); i++) {
                Node node = children.item(i);
                if (node.getNodeType() == Node.ELEMENT_NODE) {
                    UIElement child = buildElement((Element) node, null, window, ids, null);
                    if (child instanceof MenuItem) {
                        menuItem.getItems().add(child);
                    }
                }
            }
            addToParent(parent, menuItem, inheritedDock != null ? inheritedDock : dockValue(el));
            return menuItem;
        }

        UIElement element = createElement(tag);
        if (element != null) {
            applyProperties(element, el);
            connectIfNamed(element, el, window, ids);
            addToParent(parent, element, inheritedDock != null ? inheritedDock : dockValue(el));
            return element;
        }

        // Unknown containers (ToolBar, StatusBar, Menu, ...) are transparent:
        // their named descendants are attached to the nearest real panel.
        NodeList children = el.getChildNodes();
        for (int i = 0; i < children.getLength(); i++) {
            Node node = children.item(i);
            if (node.getNodeType() == Node.ELEMENT_NODE) {
                Element child = (Element) node;
                buildElement(child, parent, window, ids,
                        inheritedDock != null ? inheritedDock : dockValue(child));
            }
        }
        return null;
    }

    private static UIElement createElement(String tag) {
        switch (tag) {
            case "DockPanel":
                return new DockPanel();
            case "Canvas":
                return new Canvas();
            case "Border":
                return new Border();
            case "Viewbox":
                return new Viewbox();
            case "ToolBar":
                return new ToolBar();
            case "StatusBar":
                return new StatusBar();
            case "Menu":
                return new Menu();
            case "TextBox":
                return new TextBox();
            case "TextBlock":
                return new TextBlock();
            case "MenuItem":
                return new MenuItem();
            case "Image":
                return new Image();
            default:
                return null;
        }
    }

    private static void addToParent(Panel parent, UIElement element, String dock) {
        if (parent == null || element == null) {
            return;
        }
        parent.addChild(element);
        if (dock != null) {
            DockPanel.setDock(element, dock);
        }
    }

    private static void connectIfNamed(UIElement element, Element el, Window window, IdCounter ids) {
        String name = nameOf(el);
        if (name != null && element instanceof FrameworkElement) {
            ((FrameworkElement) element).setName(name);
        }
        if (name != null && window instanceof IComponentConnector) {
            ((IComponentConnector) window).connect(ids.next(), element);
        }
    }

    private static void applyProperties(UIElement element, Element el) {
        if (element instanceof FrameworkElement) {
            FrameworkElement fe = (FrameworkElement) element;
            setDouble(fe::setWidth, attr(el, "Width"));
            setDouble(fe::setHeight, attr(el, "Height"));
            setDouble(fe::setMinWidth, attr(el, "MinWidth"));
            setDouble(fe::setMinHeight, attr(el, "MinHeight"));
            setDouble(fe::setMaxWidth, attr(el, "MaxWidth"));
            setDouble(fe::setMaxHeight, attr(el, "MaxHeight"));
            setEnum(VerticalAlignment.class, attr(el, "VerticalAlignment"), fe::setVerticalAlignment);
            setEnum(HorizontalAlignment.class, attr(el, "HorizontalAlignment"), fe::setHorizontalAlignment);
        }
        setBoolean(el, "Focusable", element::setFocusable);
        Brush background = brush(attr(el, "Background"));
        if (background != null) {
            if (element instanceof Panel) {
                ((Panel) element).setBackground(background);
            } else if (element instanceof TextBlock) {
                ((TextBlock) element).setBackground(background);
            } else if (element instanceof Border) {
                ((Border) element).setBackground(background);
            }
        }
        if (element instanceof TextBox) {
            String text = attr(el, "Text");
            if (text != null) {
                ((TextBox) element).setText(text);
            }
        } else if (element instanceof TextBlock) {
            String text = attr(el, "Text");
            if (text != null) {
                ((TextBlock) element).setText(text);
            }
        } else if (element instanceof MenuItem) {
            String header = attr(el, "Header");
            if (header != null) {
                ((MenuItem) element).setHeader(header);
            }
        }
    }

    private static void applyCommand(MenuItem item, Element el, Window window) {
        String command = attr(el, "Command");
        if (command == null) {
            return;
        }
        Object resolved = resolveCommand(command, window);
        if (resolved != null) {
            item.setCommand(resolved);
        }
    }

    private static Object resolveCommand(String command, Window window) {
        String s = command.trim();
        int colon = s.indexOf(':');
        if (colon >= 0) {
            s = s.substring(colon + 1);
        }
        int dot = s.lastIndexOf('.');
        if (dot <= 0 || dot == s.length() - 1) {
            return null;
        }
        String className = s.substring(0, dot);
        String member = s.substring(dot + 1);
        String packageName = window.getClass().getPackage() == null
                ? "" : window.getClass().getPackage().getName();
        String fqcn = packageName.isEmpty() ? className : packageName + "." + className;
        try {
            ClassLoader loader = window.getClass().getClassLoader();
            Class<?> type = Class.forName(fqcn, true, loader);
            String getter = "get" + Character.toUpperCase(member.charAt(0)) + member.substring(1);
            try {
                Method method = type.getMethod(getter);
                return method.invoke(null);
            } catch (NoSuchMethodException ignored) {
                // try a public static field next
            }
            try {
                Field field = type.getField(member);
                return field.get(null);
            } catch (NoSuchFieldException ignored) {
                return null;
            }
        } catch (ReflectiveOperationException | RuntimeException ignored) {
            return null;
        }
    }

    private static void applyWindowAttributes(Window window, Element el) {
        String title = attr(el, "Title");
        if (title != null) {
            window.setTitle(title);
        }
        setDouble(window::setWidth, attr(el, "Width"));
        setDouble(window::setHeight, attr(el, "Height"));
        setDouble(window::setLeft, attr(el, "Left"));
        setDouble(window::setTop, attr(el, "Top"));
        setEnum(WindowState.class, attr(el, "WindowState"), window::setWindowState);
        setEnum(WindowStartupLocation.class, attr(el, "WindowStartupLocation"), window::setWindowStartupLocation);
    }

    private static Brush brush(String value) {
        if (value == null) {
            return null;
        }
        return new BrushConverter().convertFrom(value);
    }

    private static String nameOf(Element el) {
        String name = attr(el, "Name");
        if (name == null) {
            name = el.getAttributeNS(XAML_NS, "Name");
        }
        if (name == null || name.isEmpty()) {
            name = attr(el, "x:Name");
        }
        return name == null || name.isEmpty() ? null : name;
    }

    private static String attr(Element el, String name) {
        String value = el.getAttribute(name);
        return value == null || value.isEmpty() ? null : value;
    }

    private static String dockValue(Element el) {
        String dock = attr(el, "DockPanel.Dock");
        if (dock == null) {
            dock = attr(el, "Dock");
        }
        return dock;
    }

    private static String localName(Element el) {
        String local = el.getLocalName();
        return local == null ? el.getNodeName() : local;
    }

    private static void setDouble(java.util.function.DoubleConsumer setter, String value) {
        if (value == null) {
            return;
        }
        try {
            setter.accept(Double.parseDouble(value));
        } catch (NumberFormatException ignored) {
            // ignore non-numeric XAML property values
        }
    }

    private static void setBoolean(Element el, String attribute,
                                   java.util.function.Consumer<Boolean> setter) {
        String value = attr(el, attribute);
        if (value != null) {
            setter.accept(Boolean.parseBoolean(value));
        }
    }

    private static <E extends Enum<E>> void setEnum(Class<E> type, String value,
                                                    java.util.function.Consumer<E> setter) {
        if (value == null) {
            return;
        }
        try {
            setter.accept(Enum.valueOf(type, value));
        } catch (IllegalArgumentException ignored) {
            // ignore unsupported enum values
        }
    }

    // ------------------------------------------------------------------
    // default layout fallback for projects without XAML resources
    // ------------------------------------------------------------------

    private static void buildDefaultLayout(Window window) {
        if (window.getContent() != null) {
            return;
        }
        Map<Field, UIElement> fields = collectElementFields(window);
        DockPanel root = null;
        DockPanel firstDockPanel = null;
        for (Map.Entry<Field, UIElement> entry : fields.entrySet()) {
            UIElement element = entry.getValue();
            if (!(element instanceof DockPanel)) {
                continue;
            }
            String name = entry.getKey().getName();
            if (isRootPanelName(name)) {
                root = (DockPanel) element;
                break;
            }
            if (firstDockPanel == null) {
                firstDockPanel = (DockPanel) element;
            }
        }
        if (root == null) {
            root = firstDockPanel != null ? firstDockPanel : new DockPanel();
        }
        if (root.getName() == null) {
            root.setName("dockPanel");
        }

        List<UIElement> connected = new ArrayList<>();
        for (Map.Entry<Field, UIElement> entry : fields.entrySet()) {
            UIElement element = entry.getValue();
            connected.add(element);
            if (element == root) {
                continue;
            }
            String dock = defaultDock(element);
            root.addChild(element);
            if (dock != null) {
                DockPanel.setDock(element, dock);
            }
        }
        window.setContent(root);

        if (window instanceof IComponentConnector) {
            int id = 1;
            for (UIElement element : connected) {
                String name = element instanceof FrameworkElement
                        ? ((FrameworkElement) element).getName() : null;
                if (name == null) {
                    continue;
                }
                try {
                    ((IComponentConnector) window).connect(id, element);
                } catch (RuntimeException ignored) {
                    // fallback connection is best effort when generated connect
                    // ids do not match field declaration order.
                }
                id++;
            }
        }
    }

    private static Map<Field, UIElement> collectElementFields(Window window) {
        Map<Field, UIElement> result = new LinkedHashMap<>();
        for (Class<?> type = window.getClass(); type != null && type != Object.class;
             type = type.getSuperclass()) {
            for (Field field : type.getDeclaredFields()) {
                if (Modifier.isStatic(field.getModifiers()) || Modifier.isFinal(field.getModifiers())) {
                    continue;
                }
                if (!UIElement.class.isAssignableFrom(field.getType())) {
                    continue;
                }
                if (Window.class.isAssignableFrom(field.getType())) {
                    // Window.owner and similar framework fields are window
                    // references, not content elements to materialize.
                    continue;
                }
                try {
                    field.setAccessible(true);
                    Object existing = field.get(window);
                    UIElement element;
                    if (existing instanceof UIElement) {
                        element = (UIElement) existing;
                    } else {
                        element = instantiate(field.getType());
                        if (element == null) {
                            continue;
                        }
                        field.set(window, element);
                    }
                    if (element instanceof FrameworkElement
                            && ((FrameworkElement) element).getName() == null) {
                        ((FrameworkElement) element).setName(field.getName());
                    }
                    result.put(field, element);
                } catch (IllegalAccessException | RuntimeException ignored) {
                    // a field that cannot be materialized is skipped
                }
            }
        }
        return result;
    }

    private static UIElement instantiate(Class<?> type) {
        if (type == DockPanel.class) {
            return new DockPanel();
        }
        if (type == Canvas.class) {
            return new Canvas();
        }
        if (type == TextBox.class) {
            return new TextBox();
        }
        if (type == TextBlock.class) {
            return new TextBlock();
        }
        if (type == Border.class) {
            return new Border();
        }
        if (type == Viewbox.class) {
            return new Viewbox();
        }
        if (type == ToolBar.class) {
            return new ToolBar();
        }
        if (type == StatusBar.class) {
            return new StatusBar();
        }
        if (type == Menu.class) {
            return new Menu();
        }
        if (UIElement.class.isAssignableFrom(type)
                && !Modifier.isAbstract(type.getModifiers())) {
            try {
                Constructor<?> ctor = type.getDeclaredConstructor();
                ctor.setAccessible(true);
                Object value = ctor.newInstance();
                if (value instanceof UIElement) {
                    return (UIElement) value;
                }
            } catch (ReflectiveOperationException | RuntimeException ignored) {
                // not instantiable
            }
        }
        return null;
    }

    private static boolean isRootPanelName(String name) {
        if (name == null) {
            return false;
        }
        String lower = name.toLowerCase();
        return "dockpanel".equals(lower)
                || "maindockpanel".equals(lower)
                || "root".equals(lower)
                || "rootpanel".equals(lower)
                || lower.contains("root");
    }

    private static String defaultDock(UIElement element) {
        String name = element instanceof FrameworkElement
                ? ((FrameworkElement) element).getName() : "";
        if (name == null) {
            name = "";
        }
        String lower = name.toLowerCase();
        if (lower.contains("status")) {
            return "Bottom";
        }
        if (element instanceof ToolBar) {
            return "Top";
        }
        if (element instanceof StatusBar) {
            return "Bottom";
        }
        if (lower.contains("search") || element instanceof TextBox) {
            return "Top";
        }
        if (element instanceof DockPanel) {
            return null;
        }
        return "Top";
    }

    private static final class IdCounter {
        private int next = 1;

        int next() {
            return next++;
        }
    }
}
