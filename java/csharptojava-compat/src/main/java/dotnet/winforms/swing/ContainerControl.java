package dotnet.winforms.swing;

import dotnet.drawing.swing.SizeF;

/**
 * System.Windows.Forms.ContainerControl compatibility base class.
 */
public class ContainerControl extends ScrollableControl {

    private SizeF autoScaleDimensions = new SizeF(0, 0);
    private AutoScaleMode autoScaleMode = AutoScaleMode.None;
    private Form parentForm;

    public SizeF getAutoScaleDimensions() {
        return autoScaleDimensions;
    }

    public void setAutoScaleDimensions(SizeF value) {
        autoScaleDimensions = value == null ? new SizeF(0, 0) : value;
    }

    public AutoScaleMode getAutoScaleMode() {
        return autoScaleMode;
    }

    public void setAutoScaleMode(AutoScaleMode value) {
        autoScaleMode = value;
    }

    public Form getParentForm() {
        return parentForm;
    }

    public void close() {
        // no-op in the Swing-backed compat layer
    }
}
