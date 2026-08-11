package dotnet.drawing.swing.drawing2d;

import java.awt.geom.AffineTransform;
import java.awt.geom.NoninvertibleTransformException;

import dotnet.drawing.swing.Point;
import dotnet.drawing.swing.PointF;

/**
 * System.Drawing.Drawing2D.Matrix compatibility class.
 */
public class Matrix implements AutoCloseable {

    private final AffineTransform transform;

    public Matrix() {
        this.transform = new AffineTransform();
    }

    public Matrix(float m11, float m12, float m21, float m22, float dx, float dy) {
        this.transform = new AffineTransform(m11, m21, m12, m22, dx, dy);
    }

    public void invert() {
        try {
            transform.invert();
        } catch (NoninvertibleTransformException e) {
            throw new IllegalStateException("Matrix is not invertible", e);
        }
    }

    public void multiply(Matrix matrix) {
        if (matrix != null) {
            transform.preConcatenate(matrix.transform);
        }
    }

    public void transformPoints(PointF[] pts) {
        if (pts == null) {
            return;
        }
        for (PointF pt : pts) {
            if (pt == null) {
                continue;
            }
            double[] src = new double[] { pt.getX(), pt.getY() };
            transform.transform(src, 0, src, 0, 1);
            pt.setX((float) src[0]);
            pt.setY((float) src[1]);
        }
    }

    public void transformPoints(Point[] pts) {
        if (pts == null) {
            return;
        }
        for (Point pt : pts) {
            if (pt == null) {
                continue;
            }
            double[] src = new double[] { pt.getX(), pt.getY() };
            transform.transform(src, 0, src, 0, 1);
            pt.setX((int) Math.round(src[0]));
            pt.setY((int) Math.round(src[1]));
        }
    }

    @Override
    public Matrix clone() {
        return new Matrix(
                (float) transform.getScaleX(),
                (float) transform.getShearY(),
                (float) transform.getShearX(),
                (float) transform.getScaleY(),
                (float) transform.getTranslateX(),
                (float) transform.getTranslateY());
    }

    public boolean getIsInvertible() {
        return transform.getDeterminant() != 0.0;
    }

    public AffineTransform getAwtTransform() {
        return transform;
    }

    @Override
    public void close() {
    }
}