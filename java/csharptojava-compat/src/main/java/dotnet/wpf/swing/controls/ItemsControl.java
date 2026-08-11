package dotnet.wpf.swing.controls;

import dotnet.wpf.swing.FrameworkElement;

/**
 * System.Windows.Controls.ItemsControl compatibility class.
 */
public class ItemsControl extends FrameworkElement {

    private final ItemCollection items = new ItemCollection();

    public ItemCollection getItems() {
        return items;
    }
}
