package dotnet.wpf.swing.media;

import dotnet.wpf.swing.NetFormat;
import dotnet.wpf.swing.Point;
import dotnet.wpf.swing.Rect;
import dotnet.wpf.swing.Size;
import java.util.Objects;

/**
 * System.Windows.Media.Matrix compatibility value type
 * (2x3 affine matrix: M11, M12, M21, M22, OffsetX, OffsetY).
 */
public final class Matrix {

    public static final Matrix IDENTITY = new Matrix(1, 0, 0, 1, 0, 0);

    private double m11;
    private double m12;
    private double m21;
    private double m22;
    private double offsetX;
    private double offsetY;

    public Matrix() {
        this(1, 0, 0, 1, 0, 0);
    }

    public Matrix(Matrix that) {
        this.m11 = that.m11;
        this.m12 = that.m12;
        this.m21 = that.m21;
        this.m22 = that.m22;
        this.offsetX = that.offsetX;
        this.offsetY = that.offsetY;
    }

    public Matrix(double m11, double m12, double m21, double m22, double offsetX, double offsetY) {
        this.m11 = m11;
        this.m12 = m12;
        this.m21 = m21;
        this.m22 = m22;
        this.offsetX = offsetX;
        this.offsetY = offsetY;
    }

    public double getM11() { return m11; }
    public void setM11(double value) { m11 = value; }

    public double getM12() { return m12; }
    public void setM12(double value) { m12 = value; }

    public double getM21() { return m21; }
    public void setM21(double value) { m21 = value; }

    public double getM22() { return m22; }
    public void setM22(double value) { m22 = value; }

    public double getOffsetX() { return offsetX; }
    public void setOffsetX(double value) { offsetX = value; }

    public double getOffsetY() { return offsetY; }
    public void setOffsetY(double value) { offsetY = value; }

    public boolean isIdentity() {
        return close(m11, 1) && Math.abs(m12) < EPS && Math.abs(m21) < EPS
                && close(m22, 1) && Math.abs(offsetX) < EPS && Math.abs(offsetY) < EPS;
    }

    private static final double EPS = 1e-12;

    private static boolean close(double a, double b) {
        return Math.abs(a - b) < EPS;
    }

    public boolean hasInverse() {
        return Math.abs(determinant()) > 1e-12;
    }

    public double determinant() {
        return m11 * m22 - m12 * m21;
    }

    public void setIdentity() {
        m11 = 1; m12 = 0; m21 = 0; m22 = 1; offsetX = 0; offsetY = 0;
    }

    public void append(Matrix matrix) {
        Matrix r = multiply(this, matrix);
        copyFrom(r);
    }

    public void prepend(Matrix matrix) {
        Matrix r = multiply(matrix, this);
        copyFrom(r);
    }

    /**
     * .NET WPF row-vector convention: the combined matrix represents points on the
     * left, so {@code R*M} has linear part R.linear*M.linear and offset
     * {@code R.offset*M.linear + M.offset}. This makes Append(scale then translate)
     * keep the translation in device pixels (not scaled).
     */
    public static Matrix multiply(Matrix trans1, Matrix trans2) {
        return new Matrix(
                trans1.m11 * trans2.m11 + trans1.m12 * trans2.m21,
                trans1.m11 * trans2.m12 + trans1.m12 * trans2.m22,
                trans1.m21 * trans2.m11 + trans1.m22 * trans2.m21,
                trans1.m21 * trans2.m12 + trans1.m22 * trans2.m22,
                trans1.offsetX * trans2.m11 + trans1.offsetY * trans2.m21 + trans2.offsetX,
                trans1.offsetX * trans2.m12 + trans1.offsetY * trans2.m22 + trans2.offsetY);
    }

    public void invert() {
        double det = determinant();
        if (Math.abs(det) < 1e-12) {
            throw new RuntimeException("Matrix is not invertible.");
        }
        double invDet = 1.0 / det;
        double nm11 = m22 * invDet;
        double nm12 = -m12 * invDet;
        double nm21 = -m21 * invDet;
        double nm22 = m11 * invDet;
        // [x' y'] -> [ (x'-ox) (y'-oy) ] * L^-1, so Offset' = -[ox oy] * L^-1
        double noffX = (-m22 * offsetX + m21 * offsetY) * invDet;
        double noffY = (m12 * offsetX - m11 * offsetY) * invDet;
        m11 = nm11; m12 = nm12; m21 = nm21; m22 = nm22;
        offsetX = noffX; offsetY = noffY;
    }

    public void scale(double scaleX, double scaleY) {
        append(new Matrix(scaleX, 0, 0, scaleY, 0, 0));
    }

    public void scaleAt(double scaleX, double scaleY, double centerX, double centerY) {
        append(new Matrix(scaleX, 0, 0, scaleY, centerX - scaleX * centerX, centerY - scaleY * centerY));
    }

    public void scaleAtPrepend(double scaleX, double scaleY, double centerX, double centerY) {
        prepend(new Matrix(scaleX, 0, 0, scaleY, centerX - scaleX * centerX, centerY - scaleY * centerY));
    }

    public void translate(double offsetX, double offsetY) {
        append(new Matrix(1, 0, 0, 1, offsetX, offsetY));
    }

    public void translatePrepend(double offsetX, double offsetY) {
        prepend(new Matrix(1, 0, 0, 1, offsetX, offsetY));
    }

    public void rotate(double angle) {
        angle %= 360.0;
        append(createRotationRadians(Math.toRadians(angle), 0, 0));
    }

    public void rotateAt(double angle, double centerX, double centerY) {
        angle %= 360.0;
        append(createRotationRadians(Math.toRadians(angle), centerX, centerY));
    }

    public void rotatePrepend(double angle) {
        angle %= 360.0;
        prepend(createRotationRadians(Math.toRadians(angle), 0, 0));
    }

    public void rotateAtPrepend(double angle, double centerX, double centerY) {
        angle %= 360.0;
        prepend(createRotationRadians(Math.toRadians(angle), centerX, centerY));
    }

    private static Matrix createRotationRadians(double angle, double centerX, double centerY) {
        double sin = Math.sin(angle);
        double cos = Math.cos(angle);
        double dx = (centerX * (1.0 - cos)) + (centerY * sin);
        double dy = (centerY * (1.0 - cos)) - (centerX * sin);
        return new Matrix(cos, sin, -sin, cos, dx, dy);
    }

    public Point transform(Point point) {
        return new Point(
                m11 * point.getX() + m21 * point.getY() + offsetX,
                m12 * point.getX() + m22 * point.getY() + offsetY);
    }

    public Rect transform(Rect rect) {
        Point tl = transform(rect.getTopLeft());
        Point tr = transform(rect.getTopRight());
        Point bl = transform(rect.getBottomLeft());
        Point br = transform(rect.getBottomRight());
        Rect result = new Rect(tl, tr);
        result.union(bl);
        result.union(br);
        return result;
    }

    public static boolean equals(Matrix matrix1, Matrix matrix2) {
        if (matrix1 == null || matrix2 == null) return matrix1 == matrix2;
        return matrix1.equals(matrix2);
    }

    private void copyFrom(Matrix that) {
        m11 = that.m11; m12 = that.m12; m21 = that.m21; m22 = that.m22;
        offsetX = that.offsetX; offsetY = that.offsetY;
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Matrix)) return false;
        Matrix that = (Matrix) o;
        return Double.compare(that.m11, m11) == 0
                && Double.compare(that.m12, m12) == 0
                && Double.compare(that.m21, m21) == 0
                && Double.compare(that.m22, m22) == 0
                && Double.compare(that.offsetX, offsetX) == 0
                && Double.compare(that.offsetY, offsetY) == 0;
    }

    @Override
    public int hashCode() {
        return Objects.hash(m11, m12, m21, m22, offsetX, offsetY);
    }

    @Override
    public String toString() {
        if (isIdentity()) {
            return "Identity";
        }
        return NetFormat.format(m11) + "," + NetFormat.format(m12) + "," + NetFormat.format(m21)
                + "," + NetFormat.format(m22) + "," + NetFormat.format(offsetX) + ","
                + NetFormat.format(offsetY);
    }
}
