package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.BrushConverter compatibility class.
 * Converts string color names / #RRGGBB(A) to a SolidColorBrush.
 */
public final class BrushConverter {

    public boolean canConvertFrom(Class<?> sourceType) {
        return sourceType == String.class;
    }

    /**
     * Converts the given value (string) to a SolidColorBrush.
     */
    public Brush convertFrom(Object value) {
        return convertFromInternal(value);
    }

    public Brush convertFrom(Class<?> sourceType, Object value) {
        return convertFromInternal(value);
    }

    private static Brush convertFromInternal(Object value) {
        if (value == null) {
            return null;
        }
        if (value instanceof Brush) {
            return (Brush) value;
        }
        String text = value.toString();
        SolidColorBrush named = Brushes.fromName(text);
        if (named != null) {
            return named;
        }
        return new SolidColorBrush(parseColor(text));
    }

    /**
     * Parses "#RRGGBB" or "#AARRGGBB" (also without '#', and bytes as hex or decimal triple).
     */
    static Color parseColor(String text) {
        if (text == null) {
            return new Color();
        }
        String s = text.trim();
        if (s.startsWith("#")) {
            s = s.substring(1);
        }
        if (s.length() == 6 || s.length() == 8) {
            try {
                long v = Long.parseLong(s, 16);
                if (s.length() == 6) {
                    return new Color(255, (int) ((v >> 16) & 0xFF), (int) ((v >> 8) & 0xFF), (int) (v & 0xFF));
                }
                return new Color((int) ((v >> 24) & 0xFF), (int) ((v >> 16) & 0xFF),
                        (int) ((v >> 8) & 0xFF), (int) (v & 0xFF));
            } catch (NumberFormatException ignored) {
                // fall through
            }
        }
        return new Color();
    }
}
