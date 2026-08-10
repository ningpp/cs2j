package dotnet.wpf.swing.media;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class BrushesTest {

    @Test
    void namedBrushes_haveKnownColors() {
        assertEquals(Color.fromRgb(255, 0, 0), Brushes.Red.getColor());
        assertEquals(Color.fromRgb(0, 0, 255), Brushes.Blue.getColor());
        assertEquals(Color.fromArgb(255, 128, 128, 128), Brushes.Gray.getColor());
        assertEquals(Color.fromArgb(0, 0, 0, 0), Brushes.Transparent.getColor());
        assertEquals(Color.fromRgb(255, 255, 255), Brushes.White.getColor());
    }

    @Test
    void fromName_matchesCaseInsensitive() {
        assertSame(Brushes.Red, Brushes.fromName("Red"));
        assertSame(Brushes.Red, Brushes.fromName("red"));
        assertSame(Brushes.LightGoldenrodYellow, Brushes.fromName("LightGoldenrodYellow"));
        assertNull(Brushes.fromName("NotAColor"));
        assertNull(Brushes.fromName(null));
    }
}
