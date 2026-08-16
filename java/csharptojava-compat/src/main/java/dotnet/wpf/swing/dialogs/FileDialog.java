package dotnet.wpf.swing.dialogs;

import javax.swing.JFileChooser;
import javax.swing.filechooser.FileNameExtensionFilter;
import java.io.File;
import java.util.ArrayList;
import java.util.List;

/** Microsoft.Win32.FileDialog compatibility class. */
public class FileDialog extends CommonItemDialog {

    private String fileName;
    private String defaultExt;
    private String filter;

    public String getFileName() {
        return fileName;
    }

    public void setFileName(String value) {
        fileName = value;
    }

    public String getDefaultExt() {
        return defaultExt;
    }

    public void setDefaultExt(String value) {
        defaultExt = value;
    }

    public String getFilter() {
        return filter;
    }

    public void setFilter(String value) {
        filter = value;
    }

    /** Shared JFileChooser implementation for open/save dialogs. */
    protected boolean showChooser(boolean save) {
        if (java.awt.GraphicsEnvironment.isHeadless()) {
            return false;
        }
        try {
            JFileChooser chooser = new JFileChooser();
            if (fileName != null && !fileName.isEmpty()) {
                chooser.setSelectedFile(new File(fileName));
            }
            applyFilter(chooser);
            int result = save ? chooser.showSaveDialog(null) : chooser.showOpenDialog(null);
            if (result != JFileChooser.APPROVE_OPTION) {
                return false;
            }
            File selected = chooser.getSelectedFile();
            if (save && selected != null && defaultExt != null && !defaultExt.isEmpty()
                    && !hasExtension(selected)) {
                selected = new File(selected.getPath()
                        + (defaultExt.startsWith(".") ? defaultExt : "." + defaultExt));
            }
            fileName = selected == null ? null : selected.getAbsolutePath();
            return true;
        } catch (RuntimeException e) {
            return false;
        }
    }

    private void applyFilter(JFileChooser chooser) {
        if (filter == null || filter.isEmpty()) {
            return;
        }
        String[] segments = filter.split("\\|");
        for (int i = 0; i + 1 < segments.length; i += 2) {
            String description = segments[i].trim();
            String pattern = segments[i + 1].trim();
            List<String> extensions = new ArrayList<>();
            for (String part : pattern.split(";")) {
                String ext = part.trim().replace("*", "");
                int dot = ext.lastIndexOf('.');
                if (dot >= 0) {
                    ext = ext.substring(dot + 1);
                }
                if (!ext.isEmpty() && !ext.equals("*")) {
                    extensions.add(ext);
                }
            }
            if (!extensions.isEmpty()) {
                chooser.addChoosableFileFilter(
                        new FileNameExtensionFilter(description, extensions.toArray(new String[0])));
            }
        }
        if (chooser.getChoosableFileFilters().length > 0) {
            chooser.setFileFilter(chooser.getChoosableFileFilters()[0]);
        }
    }

    private static boolean hasExtension(File file) {
        String name = file.getName();
        int dot = name.lastIndexOf('.');
        return dot > 0 && dot < name.length() - 1;
    }
}
