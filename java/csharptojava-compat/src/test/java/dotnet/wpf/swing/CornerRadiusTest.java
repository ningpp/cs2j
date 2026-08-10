package dotnet.wpf.swing;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class CornerRadiusTest {

    @Test
    void uniformCtor_setsAllCorners() {
        CornerRadius r = new CornerRadius(5);
        assertEquals(5.0, r.getTopLeft());
        assertEquals(5.0, r.getTopRight());
        assertEquals(5.0, r.getBottomRight());
        assertEquals(5.0, r.getBottomLeft());
    }

    @Test
    void fourArgCtor() {
        CornerRadius r = new CornerRadius(1, 2, 3, 4);
        assertEquals(1.0, r.getTopLeft());
        assertEquals(2.0, r.getTopRight());
        assertEquals(3.0, r.getBottomRight());
        assertEquals(4.0, r.getBottomLeft());
    }

    @Test
    void equalsAndHashCode() {
        assertEquals(new CornerRadius(2.5), new CornerRadius(2.5, 2.5, 2.5, 2.5));
        assertEquals(new CornerRadius(2.5).hashCode(), new CornerRadius(2.5).hashCode());
        assertNotEquals(new CornerRadius(1), new CornerRadius(2));
    }
}
