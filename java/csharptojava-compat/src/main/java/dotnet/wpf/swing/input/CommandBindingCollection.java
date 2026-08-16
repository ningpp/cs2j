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

    public CommandBinding find(RoutedUICommand command) {
        for (CommandBinding binding : bindings) {
            if (binding.getCommand() == command) {
                return binding;
            }
        }
        return null;
    }

    public List<CommandBinding> getBindings() {
        return bindings;
    }
}
