package dotnet.wpf.swing.media;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class ColorTest {

    @Test
    void fromArgb_setsChannels() {
        Color c = Color.fromArgb(255, 10, 20, 30);
        assertEquals(255, c.getA());
        assertEquals(10, c.getR());
        assertEquals(20, c.getG());
        assertEquals(30, c.getB());
    }

    @Test
    void fromRgb_defaultsAlpha255() {
        Color c = Color.fromRgb(1, 2, 3);
        assertEquals(255, c.getA());
        assertEquals(1, c.getR());
        assertEquals(2, c.getG());
        assertEquals(3, c.getB());
    }

    @Test
    void setters_updateChannels() {
        Color c = new Color();
        c.setA(1);
        c.setR(2);
        c.setG(3);
        c.setB(4);
        assertEquals(Color.fromArgb(1, 2, 3, 4), c);
    }

    @Test
    void toAwtColor_preservesAndClamps() {
        java.awt.Color awt = Color.fromArgb(100, 300, -5, 128).toAwtColor();
        assertEquals(100, awt.getAlpha());
        assertEquals(255, awt.getRed());
        assertEquals(0, awt.getGreen());
        assertEquals(128, awt.getBlue());
    }

    @Test
    void equals_comparesByValue() {
        assertEquals(Color.fromArgb(1, 2, 3, 4), Color.fromArgb(1, 2, 3, 4));
        assertNotEquals(Color.fromArgb(1, 2, 3, 4), Color.fromArgb(2, 2, 3, 4));
    }
}
