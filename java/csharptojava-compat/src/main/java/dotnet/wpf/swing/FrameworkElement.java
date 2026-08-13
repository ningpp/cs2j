package dotnet.wpf.swing;

import dotnet.wpf.swing.media.Transform;

import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

/**
 * System.Windows.FrameworkElement compatibility base class.
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
    private Object tag;
    private Object toolTip;
    private String name;
    private Object dataContext;
    private Double margin;
    private Transform layoutTransform;
    private HorizontalAlignment horizontalAlignment = HorizontalAlignment.Stretch;
    private VerticalAlignment verticalAlignment = VerticalAlignment.Stretch;
    private final List<BiConsumer<Object, RoutedEventArgs>> loadedListeners = new ArrayList<>();

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
    }

    /** Internal: set by the Swing renderer during layout. */
    public void setActualHeight(double value) {
        actualHeight = value;
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
        dataContext = value;
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

    // ---- Loaded event ----

    public void addLoadedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        loadedListeners.add(handler);
    }

    public void removeLoadedListener(BiConsumer<Object, RoutedEventArgs> handler) {
        loadedListeners.remove(handler);
    }

    public void raiseLoaded() {
        RoutedEventArgs e = new RoutedEventArgs();
        for (BiConsumer<Object, RoutedEventArgs> h : new ArrayList<>(loadedListeners)) {
            h.accept(this, e);
        }
    }

    public boolean applyTemplate() {
        return true;
    }
}
