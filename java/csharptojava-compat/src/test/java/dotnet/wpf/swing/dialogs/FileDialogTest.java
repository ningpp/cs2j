package dotnet.wpf.swing.dialogs;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Headless-safe file dialog behavior for the Swing-backed WPF compat layer.
 */
class FileDialogTest {

    @Test
    void openFileDialog_headless_returnsFalseWithoutThrowing() {
        OpenFileDialog dialog = new OpenFileDialog();
        dialog.setFileName("before.txt");

        assertFalse(dialog.showDialog(), "a dialog that cannot be shown must not pretend to succeed");
        assertEquals("before.txt", dialog.getFileName());
    }

    @Test
    void saveFileDialog_headless_returnsFalseWithoutThrowing() {
        SaveFileDialog dialog = new SaveFileDialog();
        dialog.setFileName("before.txt");

        assertFalse(dialog.showDialog());
        assertEquals("before.txt", dialog.getFileName());
    }
}
