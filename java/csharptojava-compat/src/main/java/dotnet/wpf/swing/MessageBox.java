package dotnet.wpf.swing;

/**
 * System.Windows.MessageBox compatibility class.
 * Swing-backend: shows a JOptionPane message dialog honoring the requested
 * buttons and icon, and returns the selected {@link MessageBoxResult} value
 * (OK=1, Cancel=2, Yes=6, No=7). On headless test environments it prints
 * the message and returns OK without blocking.
 */
public final class MessageBox {

    private MessageBox() {
    }

    public static int show(String messageBoxText) {
        return show(messageBoxText, "");
    }

    public static int show(String messageBoxText, String caption) {
        return show(messageBoxText, caption, MessageBoxButton.OK);
    }

    public static int show(String messageBoxText, String caption, MessageBoxButton button) {
        return show(messageBoxText, caption, button, MessageBoxImage.None);
    }

    public static int show(String messageBoxText, String caption, MessageBoxButton button, MessageBoxImage icon) {
        return showMessageBox(null, messageBoxText, caption, button, icon);
    }

    public static int show(Window owner, String messageBoxText) {
        return show(owner, messageBoxText, "");
    }

    public static int show(Window owner, String messageBoxText, String caption) {
        return show(owner, messageBoxText, caption, MessageBoxButton.OK);
    }

    public static int show(Window owner, String messageBoxText, String caption, MessageBoxButton button) {
        return show(owner, messageBoxText, caption, button, MessageBoxImage.None);
    }

    public static int show(Window owner, String messageBoxText, String caption,
                           MessageBoxButton button, MessageBoxImage icon) {
        return showMessageBox(owner, messageBoxText, caption, button, icon);
    }

    private static int showMessageBox(Window owner, String text, String caption,
                                      MessageBoxButton button, MessageBoxImage icon) {
        if (java.awt.GraphicsEnvironment.isHeadless()) {
            System.err.println("[MessageBox] " + caption + ": " + text);
            return MessageBoxResult.OK.getValue();
        }
        MessageBoxButton b = button == null ? MessageBoxButton.OK : button;
        MessageBoxImage i = icon == null ? MessageBoxImage.None : icon;
        String[] options = optionsFor(b);
        try {
            javax.swing.JOptionPane optionPane = new javax.swing.JOptionPane(
                    text == null ? "" : text,
                    messageTypeFor(i),
                    javax.swing.JOptionPane.DEFAULT_OPTION,
                    null,
                    options,
                    options[0]);
            javax.swing.JDialog dialog = optionPane.createDialog(ownerFrame(owner), caption == null ? "" : caption);
            dialog.setModal(true);
            dialog.setVisible(true);
            Object value = optionPane.getValue();
            return resultFor(value, b);
        } catch (Throwable t) {
            System.err.println("[MessageBox] " + caption + ": " + text);
            return MessageBoxResult.OK.getValue();
        }
    }

    private static java.awt.Frame ownerFrame(Window owner) {
        if (owner == null) {
            return null;
        }
        return owner.getOwnerFrame();
    }

    private static String[] optionsFor(MessageBoxButton button) {
        switch (button) {
            case OKCancel:
                return new String[]{"OK", "Cancel"};
            case YesNo:
                return new String[]{"Yes", "No"};
            case YesNoCancel:
                return new String[]{"Yes", "No", "Cancel"};
            case OK:
            default:
                return new String[]{"OK"};
        }
    }

    private static int messageTypeFor(MessageBoxImage icon) {
        switch (icon) {
            case Error:
            case Hand:
            case Stop:
                return javax.swing.JOptionPane.ERROR_MESSAGE;
            case Question:
                return javax.swing.JOptionPane.QUESTION_MESSAGE;
            case Exclamation:
            case Warning:
                return javax.swing.JOptionPane.WARNING_MESSAGE;
            case Asterisk:
            case Information:
                return javax.swing.JOptionPane.INFORMATION_MESSAGE;
            case None:
            default:
                return javax.swing.JOptionPane.PLAIN_MESSAGE;
        }
    }

    private static int resultFor(Object value, MessageBoxButton button) {
        if (value == null) {
            return MessageBoxResult.Cancel.getValue();
        }
        if (value instanceof Integer) {
            if (((Integer) value) == javax.swing.JOptionPane.CLOSED_OPTION) {
                return MessageBoxResult.Cancel.getValue();
            }
            return MessageBoxResult.OK.getValue();
        }
        String s = String.valueOf(value);
        if ("Yes".equals(s)) {
            return MessageBoxResult.Yes.getValue();
        }
        if ("No".equals(s)) {
            return MessageBoxResult.No.getValue();
        }
        if ("Cancel".equals(s)) {
            return MessageBoxResult.Cancel.getValue();
        }
        return MessageBoxResult.OK.getValue();
    }
}
