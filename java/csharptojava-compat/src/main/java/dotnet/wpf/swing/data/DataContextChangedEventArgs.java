package dotnet.wpf.swing.data;

import dotnet.wpf.swing.RoutedEventArgs;

/**
 * System.Windows.Data.DataContextChangedEventArgs compatibility class.
 */
public class DataContextChangedEventArgs extends RoutedEventArgs {

    private final Object oldValue;
    private final Object newValue;

    public DataContextChangedEventArgs(Object oldValue, Object newValue) {
        this.oldValue = oldValue;
        this.newValue = newValue;
    }

    public Object getOldValue() {
        return oldValue;
    }

    public Object getNewValue() {
        return newValue;
    }
}