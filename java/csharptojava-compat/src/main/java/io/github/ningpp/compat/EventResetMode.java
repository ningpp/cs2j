package io.github.ningpp.compat;

/**
 * System.Threading.EventResetMode compatibility enum.
 */
public final class EventResetMode {

    public static final EventResetMode AutoReset = new EventResetMode(0);
    public static final EventResetMode ManualReset = new EventResetMode(1);

    private final int value;

    private EventResetMode(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static EventResetMode fromValue(int v) {
        return v == 0 ? AutoReset : ManualReset;
    }
}
