package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class MatrixTransformTest {

    @Test
    void ctorFromMatrix() {
        Matrix m = new Matrix(1, 0, 0, 1, 10, 20);
        MatrixTransform t = new MatrixTransform(m);
        assertEquals(m, t.getMatrix());
        assertEquals(m, t.getValue());
    }

    @Test
    void ctorFromSixDoubles() {
        MatrixTransform t = new MatrixTransform(2, 0, 0, 2, 5, 5);
        assertEquals(new Point(7, 9), t.transform(new Point(1, 2)));
    }

    @Test
    void inverse_undoesTransform() {
        MatrixTransform t = new MatrixTransform(2, 0, 0, 2, 10, 20);
        GeneralTransform inv = t.getInverse();
        Point p = new Point(3, 4);
        Point back = inv.transform(t.transform(p));
        assertEquals(p, back);
    }

    @Test
    void clone_isIndependent() {
        MatrixTransform t = new MatrixTransform(2, 0, 0, 1, 0, 0);
        MatrixTransform c = t.clone();
        c.setMatrix(new Matrix());
        assertNotEquals(t.getMatrix(), c.getMatrix());
        assertEquals(new Matrix(2, 0, 0, 1, 0, 0), t.getMatrix());
    }
}
