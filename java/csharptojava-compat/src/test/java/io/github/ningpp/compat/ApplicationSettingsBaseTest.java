package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertSame;

import org.junit.jupiter.api.Test;

/**
 * Regression for the AGL conversion: generated Settings classes extend
 * ApplicationSettingsBase and call its static synchronizedValue factory.
 */
class ApplicationSettingsBaseTest {

    @Test
    void synchronizedValueReturnsSameInstance() {
        var settings = new TestSettings();
        assertSame(settings, ApplicationSettingsBase.synchronizedValue(settings));
    }

    private static final class TestSettings extends ApplicationSettingsBase {
    }
}
