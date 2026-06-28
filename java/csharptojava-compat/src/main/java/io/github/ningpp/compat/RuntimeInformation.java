package io.github.ningpp.compat;

public final class RuntimeInformation {
    private RuntimeInformation() {
    }

    public static String getFrameworkDescription() {
        return "Java " + System.getProperty("java.version", "");
    }
}
