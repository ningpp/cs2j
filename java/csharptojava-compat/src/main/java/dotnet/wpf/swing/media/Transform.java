package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.Transform compatibility base class.
 * Runs on top of the {@link Matrix} value type.
 */
public abstract class Transform extends GeneralTransform {

    public static final Transform IDENTITY = new Transform() {
        @Override
        public GeneralTransform getInverse() { return this; }

        @Override
        protected Point transformImpl(Point point) { return point; }

        @Override
        public Matrix getValue() { return Matrix.IDENTITY; }

        @Override
        public String toString() { return "Identity"; }
    };

    public abstract Matrix getValue();

    public Transform getInverseTransform() {
        GeneralTransform inv = getInverse();
        return inv instanceof Transform ? (Transform) inv : null;
    }

    public Transform clone() {
        try {
            return (Transform) super.clone();
        } catch (CloneNotSupportedException e) {
            throw new RuntimeException(e);
        }
    }
}
