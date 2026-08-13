package dotnet.wpf.swing;

import io.github.ningpp.compat.CancelEventArgs;

/**
 * System.Windows.SessionEndingCancelEventArgs compatibility class.
 * Raised by {@link Application} when the OS session is ending; listeners
 * can {@link #setCancel(boolean)} to veto the session end.
 */
public class SessionEndingCancelEventArgs extends CancelEventArgs {

    /**
     * System.Windows.ReasonSessionEnding compatibility enum.
     */
    public enum ReasonSessionEnding {
        Logoff(0),
        SystemShutdown(1);

        private final int value;

        ReasonSessionEnding(int value) {
            this.value = value;
        }

        public int getValue() {
            return value;
        }

        public static ReasonSessionEnding fromValue(int value) {
            for (ReasonSessionEnding e : values()) {
                if (e.value == value) {
                    return e;
                }
            }
            return values()[0];
        }

        public static ReasonSessionEnding fromValueUnchecked(int value) {
            return fromValue(value);
        }
    }

    private final ReasonSessionEnding reasonSessionEnding;

    public SessionEndingCancelEventArgs(ReasonSessionEnding reasonSessionEnding) {
        this.reasonSessionEnding = reasonSessionEnding == null
                ? ReasonSessionEnding.SystemShutdown : reasonSessionEnding;
    }

    public SessionEndingCancelEventArgs() {
        this(ReasonSessionEnding.SystemShutdown);
    }

    public ReasonSessionEnding getReasonSessionEnding() {
        return reasonSessionEnding;
    }
}