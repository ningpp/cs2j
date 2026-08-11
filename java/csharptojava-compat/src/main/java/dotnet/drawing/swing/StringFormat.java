package dotnet.drawing.swing;

/**
 * System.Drawing.StringFormat compatibility class.
 */
public class StringFormat implements AutoCloseable {

    public static final StringFormat GenericTypographic = new StringFormat();

    public StringFormat() {
    }

    public static StringFormat getGenericTypographic() {
        return GenericTypographic;
    }

    @Override
    public void close() {
    }
}