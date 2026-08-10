package dotnet.wpf.swing.input;

import dotnet.wpf.swing.RoutedEventArgs;

/**
 * System.Windows.Input.KeyEventArgs compatibility class.
 */
public class KeyEventArgs extends RoutedEventArgs {

    private Key key = Key.None;

    public Key getKey() {
        return key;
    }

    public void setKey(Key value) {
        key = value;
    }

    public boolean isDown() {
        return true;
    }

    public boolean isUp() {
        return false;
    }

    public boolean isRepeat() {
        return false;
    }

    public boolean isToggled() {
        return false;
    }
}
