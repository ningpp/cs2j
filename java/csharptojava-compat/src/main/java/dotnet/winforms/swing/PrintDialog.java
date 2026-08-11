package dotnet.winforms.swing;

/**
 * System.Windows.Forms.PrintDialog compatibility class.
 */
public class PrintDialog extends CommonDialog {

    private dotnet.drawing.swing.printing.PrintDocument document;

    public PrintDialog() {
    }

    public dotnet.drawing.swing.printing.PrintDocument getDocument() {
        return document;
    }

    public void setDocument(dotnet.drawing.swing.printing.PrintDocument value) {
        document = value;
    }
}
