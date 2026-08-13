package dotnet.wpf.swing;

/**
 * System.Windows.SizeChangedEventArgs compatibility class.
 */
public class SizeChangedEventArgs extends RoutedEventArgs {

    private final Size previousSize;
    private final Size newSize;

    public SizeChangedEventArgs(Size previousSize, Size newSize) {
        this.previousSize = previousSize == null ? new Size() : previousSize;
        this.newSize = newSize == null ? new Size() : newSize;
    }

    public Size getPreviousSize() {
        return previousSize;
    }

    public Size getNewSize() {
        return newSize;
    }

    public double getPreviousWidth() {
        return previousSize.getWidth();
    }

    public double getPreviousHeight() {
        return previousSize.getHeight();
    }

    public double getNewWidth() {
        return newSize.getWidth();
    }

    public double getNewHeight() {
        return newSize.getHeight();
    }
}