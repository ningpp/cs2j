package dotnet.wpf.swing.threading;

/**
 * System.Windows.Threading.DispatcherFrame compatibility class.
 * A nested UI-loop frame whose {@link #getContinue()}/{@link #setContinue(boolean)}
 * flag controls whether {@link Dispatcher#pushFrame(DispatcherFrame)} keeps
 * running.
 */
public class DispatcherFrame {

    private boolean continueRun = true;

    public DispatcherFrame() {
    }

    /** True while the frame should keep running; set to false to exit the frame. */
    public boolean getContinue() {
        return continueRun;
    }

    public void setContinue(boolean value) {
        continueRun = value;
    }

    /** Exits this frame (System.Windows.Threading.DispatcherFrame.Exit). */
    public void exit() {
        continueRun = false;
    }
}