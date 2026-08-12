package dotnet.winforms.swing;

import io.github.ningpp.compat.StreamWrapper;

/**
 * System.Windows.Forms.Cursor compatibility class.
 */
public final class Cursor implements AutoCloseable {

    private final StreamWrapper stream;

    public Cursor(StreamWrapper stream) {
        this.stream = stream;
    }

    public StreamWrapper getStream() {
        return stream;
    }

    @Override
    public void close() {
        // no-op in the Swing-backed compat layer
    }
}
