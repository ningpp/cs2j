package dotnet.wpf.swing.input;

import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.Input.CommandBindingCollection compatibility class.
 */
public class CommandBindingCollection {

    private final List<CommandBinding> bindings = new ArrayList<>();

    public void add(CommandBinding binding) {
        bindings.add(binding);
    }
}
