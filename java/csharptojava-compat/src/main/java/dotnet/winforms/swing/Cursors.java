package dotnet.winforms.swing;

/**
 * System.Windows.Forms.Cursors compatibility class.
 */
public final class Cursors {

    private Cursors() {
    }

    public static final Cursor WaitCursor = new Cursor(null);

    public static Cursor getWaitCursor() {
        return WaitCursor;
    }
}
