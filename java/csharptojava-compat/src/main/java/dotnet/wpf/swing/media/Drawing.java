package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Rect;

/**
 * System.Windows.Media.Drawing compatibility base class (retained drawing).
 */
public abstract class Drawing {

    public abstract Rect getBounds();

    public final Drawing clone() {
        return cloneDrawing();
    }

    protected abstract Drawing cloneDrawing();
}
