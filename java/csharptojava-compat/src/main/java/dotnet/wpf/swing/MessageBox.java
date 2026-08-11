package dotnet.wpf.swing;

/**
 * System.Windows.MessageBox compatibility class.
 * Swing-backend: shows a JOptionPane message dialog; on headless test environments
 * returns OK without blocking.
 */
public final class MessageBox {

    private MessageBox() {
    }

    public static int show(String messageBoxText) {
        return showMessageBox(messageBoxText, "");
    }

    public static int show(String messageBoxText, String caption) {
        return showMessageBox(messageBoxText, caption);
    }

    public static int show(String messageBoxText, String caption, MessageBoxButton button) {
        return showMessageBox(messageBoxText, caption);
    }

    public static int show(String messageBoxText, String caption, MessageBoxButton button, MessageBoxImage icon) {
        return showMessageBox(messageBoxText, caption);
    }

    private static int showMessageBox(String text, String caption) {
        try {
            if (java.awt.GraphicsEnvironment.isHeadless()) {
                System.err.println("[MessageBox] " + caption + ": " + text);
                return 1; // OK
            }
            javax.swing.JOptionPane optionPane = new javax.swing.JOptionPane(
                    text == null ? "" : text,
                    javax.swing.JOptionPane.INFORMATION_MESSAGE);
            javax.swing.JDialog dialog = optionPane.createDialog(caption == null ? "" : caption);
            dialog.setModal(true);
            dialog.setVisible(true);
            return 1;
        } catch (Throwable t) {
            System.err.println("[MessageBox] " + caption + ": " + text);
            return 1;
        }
    }
}
