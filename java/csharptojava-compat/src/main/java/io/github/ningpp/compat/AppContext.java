package io.github.ningpp.compat;

public final class AppContext {
    private AppContext() {}

    public static String getBaseDirectory() {
        return System.getProperty("user.dir");
    }
}
