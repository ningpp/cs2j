package io.github.ningpp.compat;

/**
 * System.Configuration.ApplicationSettingsBase compatibility class.
 */
public class ApplicationSettingsBase {

    protected ApplicationSettingsBase() {
    }

    public static <T> T synchronizedValue(T value) {
        return value;
    }
}
