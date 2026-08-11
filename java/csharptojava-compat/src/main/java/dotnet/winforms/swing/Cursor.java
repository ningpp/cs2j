package dotnet.winforms.swing;

import java.io.InputStream;

/**
 * System.Windows.Forms.Cursor compatibility class.
 */
public final class Cursor implements AutoCloseable {

    private final InputStream stream;

    public Cursor(InputStream stream) {
        this.stream = stream;
    }

    public InputStream getStream() {
        return stream;
    }

    @Override
    public void close() {
        // no-op in the Swing-backed compat layer
    }
}
