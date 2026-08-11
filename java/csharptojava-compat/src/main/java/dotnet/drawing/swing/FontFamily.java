package dotnet.drawing.swing;

/**
 * System.Drawing.FontFamily compatibility class.
 */
public class FontFamily {

    public static final FontFamily GenericSerif = new FontFamily("Serif");

    private final String name;

    public FontFamily(String name) {
        this.name = name != null ? name : "SansSerif";
    }

    public static FontFamily getGenericSerif() {
        return GenericSerif;
    }

    public String getName() {
        return name;
    }
}