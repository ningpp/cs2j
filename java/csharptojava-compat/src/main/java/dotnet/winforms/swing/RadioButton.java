package dotnet.winforms.swing;

import java.util.function.BiConsumer;
import io.github.ningpp.compat.EventArgs;

/**
 * System.Windows.Forms.RadioButton compatibility class.
 */
public class RadioButton extends ButtonBase {

    private boolean checked;
    private boolean tabStop = true;

    private final java.util.List<BiConsumer<Object, EventArgs>> checkedChangedListeners = new java.util.ArrayList<>();

    public RadioButton() {
    }

    public boolean getChecked() {
        return checked;
    }

    public void setChecked(boolean value) {
        checked = value;
        fireCheckedChanged(this, null);
    }

    public boolean getTabStop() {
        return tabStop;
    }

    public void setTabStop(boolean value) {
        tabStop = value;
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
