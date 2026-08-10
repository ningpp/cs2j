package dotnet.wpf.swing.input;

import dotnet.wpf.swing.Point;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class InputTest {

    @Test
    void mouseEventArgs_buttonsAndPosition() {
        MouseButtonEventArgs e = new MouseButtonEventArgs();
        e.setLeftButton(MouseButtonState.Pressed);
        e.setPosition(new Point(10, 20));
        e.setClickCount(2);
        assertEquals(MouseButtonState.Pressed, e.getLeftButton());
        assertEquals(2, e.getClickCount());
        assertEquals(new Point(10, 20), e.getPosition());
    }

    @Test
    void mouseWheelEventArgs_delta() {
        MouseWheelEventArgs e = new MouseWheelEventArgs();
        e.setDelta(120);
        assertEquals(120, e.getDelta());
    }

    @Test
    void keyEventArgs_key() {
        KeyEventArgs e = new KeyEventArgs();
        e.setKey(Key.Space);
        assertEquals(Key.Space, e.getKey());
    }

    @Test
    void keyboard_focus() {
        Object el = new Object();
        Keyboard.focus(el);
        assertEquals(el, Keyboard.getFocusedElement());
        Keyboard.clearFocus();
        assertNull(Keyboard.getFocusedElement());
    }

    @Test
    void keyboard_modifiers_noneByDefault() {
        assertEquals(ModifierKeys.None, Keyboard.getModifiers());
    }

    @Test
    void mouse_getPosition_nonNull() {
        Point p = Mouse.getPosition(null);
        assertNotNull(p);
    }

    @Test
    void mouse_capture() {
        Object el = new Object();
        assertTrue(Mouse.capture(el));
        assertEquals(el, Mouse.getCaptured());
    }
}
