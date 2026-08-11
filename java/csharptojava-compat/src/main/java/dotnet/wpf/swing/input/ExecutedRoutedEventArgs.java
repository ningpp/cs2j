package dotnet.wpf.swing.input;

import dotnet.wpf.swing.RoutedEventArgs;

/**
 * System.Windows.Input.ExecutedRoutedEventArgs compatibility class.
 */
public class ExecutedRoutedEventArgs extends RoutedEventArgs {

    private Object parameter;
    private Object command;

    public Object getParameter() {
        return parameter;
    }

    public void setParameter(Object value) {
        parameter = value;
    }

    public Object getCommand() {
        return command;
    }
}
