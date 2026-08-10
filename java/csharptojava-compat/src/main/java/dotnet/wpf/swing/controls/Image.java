package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;
import dotnet.wpf.swing.media.ImageSource;
import dotnet.wpf.swing.media.Stretch;

/**
 * System.Windows.Controls.Image compatibility class.
 */
public class Image extends FrameworkElement {

    private ImageSource source;
    private Stretch stretch = Stretch.Uniform;

    public ImageSource getSource() {
        return source;
    }

    public void setSource(ImageSource value) {
        source = value;
    }

    public Stretch getStretch() {
        return stretch;
    }

    public void setStretch(Stretch value) {
        stretch = value;
    }
}
