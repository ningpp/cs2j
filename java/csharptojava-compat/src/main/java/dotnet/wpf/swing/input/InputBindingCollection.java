package dotnet.wpf.swing.input;

import java.util.ArrayList;
import java.util.List;

/**
 * System.Windows.Input.InputBindingCollection compatibility class.
 */
public class InputBindingCollection {

    private final List<InputBinding> bindings = new ArrayList<>();

    public void add(InputBinding binding) {
        bindings.add(binding);
    }
}
