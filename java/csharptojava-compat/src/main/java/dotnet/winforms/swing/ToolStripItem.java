package dotnet.winforms.swing;

import java.util.function.BiConsumer;
import io.github.ningpp.compat.EventArgs;

/**
 * System.Windows.Forms.ToolStripItem compatibility base class.
 */
public class ToolStripItem {

    private ToolStripItemDisplayStyle displayStyle = ToolStripItemDisplayStyle.ImageAndText;
    private boolean enabled = true;
    private int imageIndex = -1;
    private String imageKey;
    private String name;
    private String text;
    private String toolTipText;
    private boolean visible = true;

    private final java.util.List<BiConsumer<Object, EventArgs>> clickListeners = new java.util.ArrayList<>();

    public ToolStripItemDisplayStyle getDisplayStyle() {
        return displayStyle;
    }

    public void setDisplayStyle(ToolStripItemDisplayStyle value) {
        displayStyle = value;
    }

    public boolean getEnabled() {
        return enabled;
    }

    public void setEnabled(boolean value) {
        enabled = value;
    }

    public int getImageIndex() {
        return imageIndex;
    }

    public void setImageIndex(int value) {
        imageIndex = value;
    }

    public String getImageKey() {
        return imageKey;
    }

    public void setImageKey(String value) {
        imageKey = value;
    }

    public String getName() {
        return name;
    }

    public void setName(String value) {
        name = value;
    }

    public String getText() {
        return text;
    }

    public void setText(String value) {
        text = value;
    }

    public String getToolTipText() {
        return toolTipText;
    }

    public void setToolTipText(String value) {
        toolTipText = value;
    }

    public boolean getVisible() {
        return visible;
    }

    public void setVisible(boolean value) {
        visible = value;
    }

    public void addClickListener(BiConsumer<Object, EventArgs> handler) {
        if (handler != null) clickListeners.add(handler);
    }

    public void removeClickListener(BiConsumer<Object, EventArgs> handler) {
        clickListeners.remove(handler);
    }

    protected void fireClick(Object sender, EventArgs args) {
        for (BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(clickListeners)) {
            h.accept(sender, args);
        }
    }
}
