package dotnet.winforms.swing;

import java.util.function.BiConsumer;
import io.github.ningpp.compat.EventArgs;

/**
 * System.Windows.Forms.TrackBar compatibility class.
 */
public class TrackBar extends Control implements ISupportInitialize {

    private int maximum = 10;
    private int minimum;
    private int tickFrequency = 1;
    private TickStyle tickStyle = TickStyle.BottomRight;
    private int value;

    private final java.util.List<BiConsumer<Object, EventArgs>> valueChangedListeners = new java.util.ArrayList<>();

    public TrackBar() {
    }

    @Override
    public void beginInit() {
        // no-op
    }

    @Override
    public void endInit() {
        // no-op
    }

    public int getMaximum() {
        return maximum;
    }

    public void setMaximum(int v) {
        maximum = v;
    }

    public int getMinimum() {
        return minimum;
    }

    public void setMinimum(int v) {
        minimum = v;
    }

    public int getTickFrequency() {
        return tickFrequency;
    }

    public void setTickFrequency(int v) {
        tickFrequency = v;
    }

    public TickStyle getTickStyle() {
        return tickStyle;
    }

    public void setTickStyle(TickStyle v) {
        tickStyle = v;
    }

    public int getValue() {
        return value;
    }

    public void setValue(int v) {
        value = v;
        fireValueChanged(this, null);
    }

    public void addValueChangedListener(BiConsumer<Object, EventArgs> handler) {
        if (handler != null) valueChangedListeners.add(handler);
    }

    public void removeValueChangedListener(BiConsumer<Object, EventArgs> handler) {
        valueChangedListeners.remove(handler);
    }

    protected void fireValueChanged(Object sender, EventArgs args) {
        for (BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(valueChangedListeners)) {
            h.accept(sender, args);
        }
    }
}
