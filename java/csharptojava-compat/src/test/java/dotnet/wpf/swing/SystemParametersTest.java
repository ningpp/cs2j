package dotnet.wpf.swing;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Headless-safe SystemParameters behavior.
 */
class SystemParametersTest {

    @Test
    void getWorkArea_doesNotThrowWhenHeadless() {
        Rect workArea = SystemParameters.getWorkArea();

        assertNotNull(workArea);
        assertTrue(workArea.getWidth() > 0, "fallback work area must have positive width");
        assertTrue(workArea.getHeight() > 0, "fallback work area must have positive height");
    }

    @Test
    void screenSize_doesNotThrowWhenHeadless() {
        assertTrue(SystemParameters.getPrimaryScreenWidth() > 0);
        assertTrue(SystemParameters.getPrimaryScreenHeight() > 0);
    }
}
