package dotnet.wpf.swing.media;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class SolidColorBrushTest {

    @Test
    void ctor_storesColor() {
        SolidColorBrush b = new SolidColorBrush(Color.fromArgb(255, 1, 2, 3));
        assertEquals(Color.fromArgb(255, 1, 2, 3), b.getColor());
    }

    @Test
    void defaultOpacity_isOne() {
        assertEquals(1.0, new SolidColorBrush().getOpacity());
    }

    @Test
    void toAwtColor_mapsChannels() {
        java.awt.Color awt = new SolidColorBrush(Color.fromArgb(200, 10, 20, 30)).toAwtColor();
        assertEquals(200, awt.getAlpha());
        assertEquals(10, awt.getRed());
        assertEquals(20, awt.getGreen());
        assertEquals(30, awt.getBlue());
    }

    @Test
    void toAwtColor_appliesOpacity() {
        SolidColorBrush b = new SolidColorBrush(Color.fromArgb(128, 255, 255, 255));
        b.setOpacity(0.5);
        java.awt.Color awt = b.toAwtColor();
        assertEquals(64, awt.getAlpha());
    }
}
