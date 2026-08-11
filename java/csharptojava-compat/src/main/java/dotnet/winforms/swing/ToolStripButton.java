package dotnet.winforms.swing;

import java.util.function.BiConsumer;
import io.github.ningpp.compat.EventArgs;

/**
 * System.Windows.Forms.ToolStripButton compatibility class.
 */
public class ToolStripButton extends ToolStripItem {

    private boolean checkOnClick;
    private boolean checked;

    private final java.util.List<BiConsumer<Object, EventArgs>> checkedChangedListeners = new java.util.ArrayList<>();

    public ToolStripButton() {
    }

    public boolean getCheckOnClick() {
        return checkOnClick;
    }

    public void setCheckOnClick(boolean value) {
        checkOnClick = value;
    }

    public boolean getChecked() {
        return checked;
    }

    public void setChecked(boolean value) {
        checked = value;
        fireCheckedChanged(this, null);
    }

    public void addCheckedChangedListener(BiConsumer<Object, EventArgs> handler) {
        if (handler != null) checkedChangedListeners.add(handler);
    }

    public void removeCheckedChangedListener(BiConsumer<Object, EventArgs> handler) {
        checkedChangedListeners.remove(handler);
    }

    protected void fireCheckedChanged(Object sender, EventArgs args) {
        for (BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(checkedChangedListeners)) {
            h.accept(sender, args);
        }
    }
}
