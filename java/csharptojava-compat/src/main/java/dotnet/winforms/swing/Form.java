package dotnet.winforms.swing;

import dotnet.drawing.swing.Icon;
import dotnet.drawing.swing.Size;

/**
 * System.Windows.Forms.Form compatibility class.
 */
public class Form extends ContainerControl {

    private IButtonControl acceptButton;
    private IButtonControl cancelButton;
    private Size clientSize = new Size(0, 0);
    private Icon icon;
    private Size size = new Size(0, 0);
    private FormStartPosition startPosition = FormStartPosition.WindowsDefaultLocation;
    private String text;
    private boolean topLevel = true;

    public Form() {
    }

    public IButtonControl getAcceptButton() {
        return acceptButton;
    }

    public void setAcceptButton(IButtonControl value) {
        acceptButton = value;
    }

    public IButtonControl getCancelButton() {
        return cancelButton;
    }

    public void setCancelButton(IButtonControl value) {
        cancelButton = value;
    }

    public Size getClientSize() {
        return clientSize;
    }

    public void setClientSize(Size value) {
        clientSize = value == null ? new Size(0, 0) : value;
    }

    public Icon getIcon() {
        return icon;
    }

    public void setIcon(Icon value) {
        icon = value;
    }

    public Size getSize() {
        return size;
    }

    public void setSize(Size value) {
        size = value == null ? new Size(0, 0) : value;
    }

    public FormStartPosition getStartPosition() {
        return startPosition;
    }

    public void setStartPosition(FormStartPosition value) {
        startPosition = value;
    }

    public String getText() {
        return text;
    }

    public void setText(String value) {
        text = value;
    }

    public boolean getTopLevel() {
        return topLevel;
    }

    public void setTopLevel(boolean value) {
        topLevel = value;
    }

    public DialogResult getDialogResult() {
        return DialogResult.None;
    }

    public void setDialogResult(DialogResult value) {
    }

    public DialogResult showDialog() {
        return DialogResult.OK;
    }

    protected void dispose(boolean disposing) {
    }
}
