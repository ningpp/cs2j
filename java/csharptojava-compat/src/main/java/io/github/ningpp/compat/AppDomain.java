package io.github.ningpp.compat;

/**
 * Minimal compat stub for System.AppDomain.
 * Mirrors the API surface used by CorrectnessTest.
 */
public class AppDomain {
    private static final AppDomain currentDomain = new AppDomain();

    private AppDomain() {
    }

    /** Mirrors C# AppDomain.CurrentDomain */
    public static AppDomain getCurrentDomain() {
        return currentDomain;
    }

    /** Mirrors C# AppDomain.BaseDirectory — returns current working directory */
    public String getBaseDirectory() {
        return System.getProperty("user.dir");
    }
}
