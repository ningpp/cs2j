package dotnet.drawing.swing;

/**
 * System.Drawing.Font compatibility class.
 */
public class Font {

    private final java.awt.Font awtFont;
    private final String familyName;
    private final float emSize;

    public Font(String familyName, float emSize) {
        this(familyName, emSize, FontStyle.Regular);
    }

    public Font(FontFamily family, float emSize) {
        this(family.getName(), emSize);
    }

    public Font(FontFamily family, float emSize, FontStyle style) {
        this(family.getName(), emSize, style);
    }

    public Font(String familyName, float emSize, FontStyle style) {
        this.familyName = familyName != null ? familyName : "SansSerif";
        this.emSize = emSize;
        this.awtFont = new java.awt.Font(this.familyName, toAwtStyle(style), Math.round(emSize));
    }

    public Font(String familyName, float emSize, int style) {
        this(familyName, emSize, FontStyle.fromValue(style));
    }

    private static int toAwtStyle(FontStyle style) {
        if (style == null) {
            return java.awt.Font.PLAIN;
        }
        switch (style) {
            case Bold:
                return java.awt.Font.BOLD;
            case Italic:
                return java.awt.Font.ITALIC;
            default:
                return java.awt.Font.PLAIN;
        }
    }

    public java.awt.Font getAwtFont() {
        return awtFont;
    }

    public float getSize() {
        return emSize;
    }

    public String getFontFamily() {
        return familyName;
    }

    public String getName() {
        return familyName;
    }
}