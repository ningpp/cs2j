package dotnet.winforms.swing;

import java.util.function.BiConsumer;

/**
 * System.Windows.Forms.ToolStrip compatibility class.
 */
public class ToolStrip extends Control {

    private ImageList imageList;
    private final ToolStripItemCollection items = new ToolStripItemCollection();
    private boolean showItemToolTips;

    private final java.util.List<ToolStripItemClickedEventHandler> itemClickedListeners = new java.util.ArrayList<>();

    public ToolStrip() {
    }

    public ImageList getImageList() {
        return imageList;
    }

    public void setImageList(ImageList value) {
        imageList = value;
    }

    public ToolStripItemCollection getItems() {
        return items;
    }

    public boolean getShowItemToolTips() {
        return showItemToolTips;
    }

    public void setShowItemToolTips(boolean value) {
        showItemToolTips = value;
    }

    public void addItemClickedListener(ToolStripItemClickedEventHandler handler) {
        if (handler != null) itemClickedListeners.add(handler);
    }

    public void removeItemClickedListener(ToolStripItemClickedEventHandler handler) {
        itemClickedListeners.remove(handler);
    }

    protected void fireItemClicked(Object sender, ToolStripItemClickedEventArgs args) {
        for (ToolStripItemClickedEventHandler h : new java.util.ArrayList<>(itemClickedListeners)) {
            h.accept(sender, args);
        }
    }

    /**
     * System.Windows.Forms.ToolStrip.ToolStripItemCollection compatibility class.
     */
    public static class ToolStripItemCollection implements Iterable<ToolStripItem> {

        private final java.util.List<ToolStripItem> items = new java.util.ArrayList<>();

        public void add(ToolStripItem item) {
            if (item != null) {
                items.add(item);
            }
        }

        public void addRange(ToolStripItem[] itemsArray) {
            if (itemsArray == null) return;
            for (ToolStripItem item : itemsArray) {
                add(item);
            }
        }

        public boolean contains(ToolStripItem item) {
            return items.contains(item);
        }

        public void remove(ToolStripItem item) {
            items.remove(item);
        }

        public int getCount() {
            return items.size();
        }

        public ToolStripItem get(int index) {
            return items.get(index);
        }

        @Override
        public java.util.Iterator<ToolStripItem> iterator() {
            return items.iterator();
        }
    }
}
