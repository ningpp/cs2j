package dotnet.wpf.swing.media;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class BrushConverterTest {

    @Test
    void convertFrom_namedColor() {
        assertNotNull((SolidColorBrush) new BrushConverter().convertFrom("Red"));
        assertNotNull((SolidColorBrush) new BrushConverter().convertFrom("Black"));
    }

    @Test
    void convertFrom_hex() {
        SolidColorBrush b = (SolidColorBrush) new BrushConverter().convertFrom("#ff0000");
        assertEquals(Color.fromRgb(255, 0, 0), b.getColor());
    }

    @Test
    void convertFrom_hexWithAlpha() {
        SolidColorBrush b = (SolidColorBrush) new BrushConverter().convertFrom("#80ff0000");
        assertEquals(0x80, b.getColor().getA());
        assertEquals(255, b.getColor().getR());
    }

    @Test
    void convertFrom_null_returnsNull() {
        assertNull(new BrushConverter().convertFrom(null));
    }

    @Test
    void convertFrom_brushPassthrough() {
        Brush b = Brushes.Blue;
        assertSame(b, new BrushConverter().convertFrom(b));
    }
}
