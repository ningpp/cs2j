package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.PathSegment compatibility base class.
 */
public abstract class PathSegment {

    private boolean isStroked;
    private boolean isSmoothJoin;

    public boolean getIsStroked() {
        return isStroked;
    }

    public void setIsStroked(boolean value) {
        isStroked = value;
    }

    public boolean getIsSmoothJoin() {
        return isSmoothJoin;
    }

    public void setIsSmoothJoin(boolean value) {
        isSmoothJoin = value;
    }

    public PathSegment clone() {
        try {
            return (PathSegment) super.clone();
        } catch (CloneNotSupportedException e) {
            throw new RuntimeException(e);
        }
    }
}
