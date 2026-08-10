package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.DrawingVisual compatibility class.
 */
public class DrawingVisual extends Visual {

    private DrawingContext openContext;

    public DrawingContext renderOpen() {
        if (openContext != null) {
            openContext.close();
        }
        openContext = new DrawingContext();
        return openContext;
    }

    public Drawing getDrawing() {
        return null;
    }
}
