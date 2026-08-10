package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.MatrixTransform compatibility class.
 */
public final class MatrixTransform extends Transform {

    private Matrix matrix;

    public MatrixTransform() {
        this(new Matrix());
    }

    public MatrixTransform(Matrix matrix) {
        this.matrix = matrix;
    }

    public MatrixTransform(double m11, double m12, double m21, double m22, double offsetX, double offsetY) {
        this.matrix = new Matrix(m11, m12, m21, m22, offsetX, offsetY);
    }

    public Matrix getMatrix() {
        return matrix;
    }

    public void setMatrix(Matrix value) {
        this.matrix = value;
    }

    @Override
    public Matrix getValue() {
        return matrix;
    }

    @Override
    public GeneralTransform getInverse() {
        Matrix m = new Matrix(matrix);
        m.invert();
        return new MatrixTransform(m);
    }

    @Override
    protected Point transformImpl(Point point) {
        return matrix.transform(point);
    }

    @Override
    public MatrixTransform clone() {
        return new MatrixTransform(new Matrix(matrix));
    }

    @Override
    public String toString() {
        return "MatrixTransform: " + matrix;
    }
}
