package io.github.ningpp.compat;

/**
 * Compat for System.OperatingSystem (runtime platform probes).
 */
public final class OperatingSystem {
    private OperatingSystem() {
    }

    private static boolean isOs(String name) {
        return System.getProperty("os.name", "").toLowerCase().contains(name);
    }

    public static boolean isWindows() {
        return isOs("win");
    }

    public static boolean isLinux() {
        return isOs("linux");
    }

    public static boolean isMacOS() {
        return isOs("mac");
    }

    public static boolean isFreeBSD() {
        return isOs("freebsd");
    }

    public static boolean isAndroid() {
        return System.getProperty("java.vendor", "").toLowerCase().contains("android");
    }

    public static boolean isIOS() {
        return false;
    }

    public static boolean isBrowser() {
        return false;
    }

    public static boolean isWasi() {
        return false;
    }
}
