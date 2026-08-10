package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class MatrixTest {
    @Test
    void defaultIsIdentity() {
        Matrix m = new Matrix();
        assertTrue(m.isIdentity());
        assertArrayEquals(new double[]{1, 0, 0, 1, 0, 0}, new double[]{
                m.getM11(), m.getM12(), m.getM21(), m.getM22(), m.getOffsetX(), m.getOffsetY()}, 1e-12);
    }

    @Test
    void transformPoint() {
        Matrix m = new Matrix(2, 0, 0, 2, 10, 20);
        Point p = m.transform(new Point(3, 4));
        assertEquals(new Point(16, 28), p);
    }

    @Test
    void invert_roundTrip() {
        Matrix m = new Matrix(2, 1, 1, 3, 5, -7);
        Matrix inv = new Matrix(m);
        inv.invert();
        Matrix roundTrip = Matrix.multiply(m, inv);
        assertTrue(roundTrip.isIdentity());
    }

    @Test
    void translate() {
        Matrix m = new Matrix();
        m.translate(5, -3);
        assertEquals(new Point(6, -1), m.transform(new Point(1, 2)));
    }

    @Test
    void scale() {
        Matrix m = new Matrix();
        m.scale(2, 3);
        assertEquals(new Point(2, 6), m.transform(new Point(1, 2)));
    }

    @Test
    void rotate90() {
        Matrix m = new Matrix();
        m.rotate(90);
        Point p = m.transform(new Point(1, 0));
        assertEquals(0.0, p.getX(), 1e-9);
        assertEquals(1.0, p.getY(), 1e-9);
    }

    @Test
    void transformRect() {
        Matrix m = new Matrix();
        m.scale(2, 2);
        Rect t = m.transform(new Rect(0, 0, 10, 10));
        assertEquals(new Rect(0, 0, 20, 20), t);
    }

    @Test
    void hasInverse() {
        assertTrue(new Matrix(2, 0, 0, 2, 0, 0).hasInverse());
        assertFalse(new Matrix(0, 0, 0, 0, 0, 0).hasInverse());
    }

    @Test
    void append_vs_prepend_order() {
        // .NET: Append applies the new transform AFTER the current one (offset in device px, unscaled).
        Matrix append = new Matrix();
        append.scale(2, 2);
        append.translate(10, 0);
        assertEquals(new Matrix(2, 0, 0, 2, 10, 0), append);

        Matrix prepend = new Matrix();
        prepend.scale(2, 2);
        prepend.translatePrepend(10, 0);
        assertEquals(new Matrix(2, 0, 0, 2, 20, 0), prepend);
    }

    @Test
    void appendAndPrepend_translation() {
        Matrix append = new Matrix(1, 0, 0, 1, 5, 5);
        append.append(new Matrix(1, 0, 0, 1, 3, 4));
        assertEquals(new Matrix(1, 0, 0, 1, 8, 9), append);

        Matrix prepend = new Matrix(1, 0, 0, 1, 5, 5);
        prepend.prepend(new Matrix(1, 0, 0, 1, 3, 4));
        assertEquals(new Matrix(1, 0, 0, 1, 8, 9), prepend);
    }

    @Test
    void rotateAt_matchesDotNet() {
        Matrix m = new Matrix();
        m.rotateAt(90, 5, 5);
        // .NET CreateRotationRadians(90°, 5,5): dx = 5(1-cos) + 5*sin = 10; dy = 5(1-cos) - 5*sin ≈ 0
        assertEquals(6.123233995736766E-17, m.getM11(), 1e-12);
        assertEquals(1.0, m.getM12(), 1e-12);
        assertEquals(-1.0, m.getM21(), 1e-12);
        assertEquals(6.123233995736766E-17, m.getM22(), 1e-12);
        assertEquals(10.0, m.getOffsetX(), 1e-12);
        assertEquals(0.0, m.getOffsetY(), 1e-12);
        Point p = m.transform(new Point(5, 5));
        assertEquals(5.0, p.getX(), 1e-9);
        assertEquals(5.0, p.getY(), 1e-9);
    }

    @Test
    void invert_asymmetric_matchesDotNet() {
        Matrix m = new Matrix(2, 3, 4, 5, 7, 11);
        Matrix inv = new Matrix(m);
        inv.invert();
        // .NET: -2.5,1.5,2,-1,-4.5,0.5
        assertEquals(new Matrix(-2.5, 1.5, 2, -1, -4.5, 0.5), inv);
        // roundtrip
        Point back = m.transform(inv.transform(new Point(1, 1)));
        assertEquals(1.0, back.getX(), 1e-9);
        assertEquals(1.0, back.getY(), 1e-9);
    }

    @Test
    void toString_matchesDotNetFormat() {
        Matrix m = new Matrix();
        m.scale(2, 3);
        assertEquals("2,0,0,3,0,0", m.toString());
        assertEquals("Identity", new Matrix().toString());
    }
}
