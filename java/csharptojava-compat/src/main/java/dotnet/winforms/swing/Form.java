package dotnet.winforms.swing;

import io.github.ningpp.compat.EventArgs;
import dotnet.drawing.swing.Icon;
import dotnet.drawing.swing.Size;
import java.util.function.BiConsumer;

/**
 * System.Windows.Forms.Form compatibility class.
 */
public class Form extends ContainerControl {

    private IButtonControl acceptButton;
    private IButtonControl cancelButton;
    private Size clientSize = new Size(0, 0);
    private Icon icon;
    private ToolStrip mainMenuStrip;
    private Size size = new Size(0, 0);
    private FormStartPosition startPosition = FormStartPosition.WindowsDefaultLocation;
    private String text;
    private boolean topLevel = true;

    private final java.util.List<BiConsumer<Object, EventArgs>> loadListeners = new java.util.ArrayList<>();

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

    public ToolStrip getMainMenuStrip() {
        return mainMenuStrip;
    }

    public void setMainMenuStrip(ToolStrip value) {
        mainMenuStrip = value;
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

    public void addLoadListener(BiConsumer<Object, EventArgs> handler) {
        if (handler != null) loadListeners.add(handler);
    }

    public void removeLoadListener(BiConsumer<Object, EventArgs> handler) {
        loadListeners.remove(handler);
    }

    protected void fireLoad(Object sender, EventArgs args) {
        for (BiConsumer<Object, EventArgs> h : new java.util.ArrayList<>(loadListeners)) {
            h.accept(sender, args);
        }
    }

    protected void dispose(boolean disposing) {
    }
}
