package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.ContentControl;

/**
 * System.Windows.Controls.ToolTip compatibility class.
 */
public class ToolTip extends ContentControl {

    private boolean isOpen;

    public boolean getIsOpen() {
        return isOpen;
    }

    public void setIsOpen(boolean value) {
        isOpen = value;
    }
}
