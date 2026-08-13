package dotnet.wpf.swing;

import dotnet.wpf.swing.data.DataContextChangedEventArgs;
import dotnet.wpf.swing.media.Transform;

import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

/**
 * System.Windows.FrameworkElement compatibility base class.
 *
 * <p>Implements the WPF element lifecycle: {@code Initialized} (raised once,
 * before the first layout), {@code Loaded} (raised after the first layout
 * pass of a shown window), {@code Unloaded} (raised when the window hides
 * or closes), {@code SizeChanged} (raised whenever the laid-out size
 * changes) and {@code DataContextChanged}.</p>
 */
public class FrameworkElement extends UIElement {

    private double width = Double.NaN;
    private double height = Double.NaN;
    private double minWidth;
    private double minHeight;
    private double maxWidth = Double.POSITIVE_INFINITY;
    private double maxHeight = Double.POSITIVE_INFINITY;
    private double actualWidth;
    private double actualHeight;
    private double lastNotifiedWidth;
    private double lastNotifiedHeight;
    private Object tag;
    private Object toolTip;
    private String name;
    private Object dataContext;
    private Double margin;
    private Transform layoutTransform;
    private HorizontalAlignment horizontalAlignment = HorizontalAlignment.Stretch;
    private VerticalAlignment verticalAlignment = VerticalAlignment.Stretch;
    private boolean isLoaded;
    private boolean initializedRaised;
    private final List<BiConsumer<Object, RoutedEventArgs>> loadedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> unloadedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RoutedEventArgs>> initializedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, SizeChangedEventArgs>> sizeChangedListeners = new ArrayList<>();
    private final List<BiConsumer<Object, DataContextChangedEventArgs>> dataContextChangedListeners = new ArrayList<>();

    public double getWidth() {
        return width;
    }

    public void setWidth(double value) {
        width = value;
        actualWidth = Double.isNaN(value) ? 0 : value;
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public double getHeight() {
        return height;
    }

    public void setHeight(double value) {
        height = value;
        actualHeight = Double.isNaN(value) ? 0 : value;
        dotnet.wpf.swing.internal.ScenePanel.refresh(this);
    }

    public double getMinWidth() {
        return minWidth;
    }

    public void setMinWidth(double value) {
        minWidth = value;
    }

    public double getMinHeight() {
        return minHeight;
    }

    public void setMinHeight(double value) {
        minHeight = value;
    }

    public double getMaxWidth() {
        return maxWidth;
    }

    public void setMaxWidth(double value) {
        maxWidth = value;
    }

    public double getMaxHeight() {
        return maxHeight;
    }

    public void setMaxHeight(double value) {
        maxHeight = value;
    }

    public double getActualWidth() {
        return actualWidth;
    }

    public double getActualHeight() {
        return actualHeight;
    }

    /** Internal: set by the Swing renderer during layout. */
    public void setActualWidth(double value) {
        actualWidth = value;
        notifySizeChanged();
    }

    /** Internal: set by the Swing renderer during layout. */
    public void setActualHeight(double value) {
        actualHeight = value;
        notifySizeChanged();
    }

    private void notifySizeChanged() {
        double prevW = lastNotifiedWidth;
        double prevH = lastNotifiedHeight;
        if (prevW == actualWidth && prevH == actualHeight) {
            return;
        }
        lastNotifiedWidth = actualWidth;
        lastNotifiedHeight = actualHeight;
        SizeChangedEventArgs e = new SizeChangedEventArgs(new Size(prevW, prevH), new Size(actualWidth, actualHeight));
        for (BiConsumer<Object, SizeChangedEventArgs> h : new ArrayList<>(sizeChangedListeners)) {
            h.accept(this, e);
        }
    }

    public Object getTag() {
        return tag;
    }

    public void setTag(Object value) {
        tag = value;
    }

    public Object getToolTip() {
        return toolTip;
    }

    public void setToolTip(Object value) {
        toolTip = value;
    }

    public String getName() {
        return name;
    }

    public void setName(String value) {
        name = value;
    }

    public Object getDataContext() {
        return dataContext;
    }

    public void setDataContext(Object value) {
        Object oldValue = dataContext;
        if (oldValue == value) {
            return;
        }
        dataContext = value;
        DataContextChangedEventArgs e = new DataContextChangedEventArgs(oldValue, value);
        for (BiConsumer<Object, DataContextChangedEventArgs> h : new ArrayList<>(dataContextChangedListeners)) {
            h.accept(this, e);
        }
    }

    public Double getMargin() {
        return margin;
    }

    public void setMargin(Double value) {
        margin = value;
    }

    public Transform getLayoutTransform() {
        return layoutTransform;
    }

    public void setLayoutTransform(Transform value) {
        layoutTransform = value;
    }

    public HorizontalAlignment getHorizontalAlignment() {
        return horizontalAlignment;
    }

    public void setHorizontalAlignment(HorizontalAlignment value) {
        horizontalAlignment = value;
    }

    public VerticalAlignment getVerticalAlignment() {
        return verticalAlignment;
    }

    public void setVerticalAlignment(VerticalAlignment value) {
        verticalAlignment = value;
    }

    // ---- Loaded / Unloaded lifecycle ----

    public boolean getIsLoaded() {
        return isLoaded;
    }

    public void addLoadedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        loadedListeners.add(handler);
    }

    public void removeLoadedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        loadedListeners.remove(handler);
    }

    public void raiseLoaded() {
        raiseInitialized();
        isLoaded = true;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(loadedListeners)) {
            h.accept(this, e);
        }
    }

    public void addUnloadedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        unloadedListeners.add(handler);
    }

    public void removeUnloadedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        unloadedListeners.remove(handler);
    }

    public void raiseUnloaded() {
        if (!isLoaded) {
            return;
        }
        isLoaded = false;
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(unloadedListeners)) {
            h.accept(this, e);
        }
    }

    // ---- Initialized lifecycle ----

    public void addInitializedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        initializedListeners.add(handler);
    }

    public void removeInitializedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        initializedListeners.remove(handler);
    }

    /** Raised once before the first layout; overridable hook is {@link #onInitialized()}. */
    public void raiseInitialized() {
        if (initializedRaised) {
            return;
        }
        initializedRaised = true;
        onInitialized();
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(initializedListeners)) {
            h.accept(this, e);
        }
    }

    /** System.Windows.FrameworkElement.OnInitialized counterpart. */
    protected void onInitialized() {
        // overridable hook
    }

    // ---- SizeChanged lifecycle ----

    public void addSizeChangedListener(BiConsumer<Object, SizeChangedEventArgs> handler) {
        sizeChangedListeners.add(handler);
    }

    public void removeSizeChangedListener(BiConsumer<Object, SizeChangedEventArgs> handler) {
        sizeChangedListeners.remove(handler);
    }

    // ---- DataContextChanged lifecycle ----

    public void addDataContextChangedListener(BiConsumer<Object, DataContextChangedEventArgs> handler) {
        dataContextChangedListeners.add(handler);
    }

    public void removeDataContextChangedListener(BiConsumer<Object, DataContextChangedEventArgs> handler) {
        dataContextChangedListeners.remove(handler);
    }

    // ---- layout helpers ----

    /** Brings the element into view (no scrolling in the Swing-backed layer). */
    public void bringIntoView() {
        // no scrolling support; the element is always painted when visible
    }

    /** Searches the visual tree rooted at this element for a named element. */
    public Object findName(String elementName) {
        if (elementName == null) {
            return null;
        }
        if (elementName.equals(name)) {
            return this;
        }
        return dotnet.wpf.swing.internal.ScenePanel.findByName(this, elementName);
    }

    public boolean applyTemplate() {
        return true;
    }
}
