package dotnet.winforms.swing;

/**
 * System.ComponentModel.Container compatibility class.
 */
public class Container implements IContainer {

    public void close() {
        // no-op in the Swing-backed compat layer
    }
}
