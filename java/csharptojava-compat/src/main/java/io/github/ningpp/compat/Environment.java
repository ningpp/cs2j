package io.github.ningpp.compat;

/**
 * Compat stub for System.Environment.
 */
public final class Environment {

    private Environment() {
    }

    public static final String NEW_LINE = System.lineSeparator();

    public static int getCurrentManagedThreadId() {
        return (int) Thread.currentThread().getId();
    }

    public static String getNewLine() {
        return NEW_LINE;
    }

    public static int getTickCount() {
        return (int) (System.currentTimeMillis() & 0xFFFFFFFFL);
    }

    public static long getTickCount64() {
        return System.currentTimeMillis();
    }

    public static int getProcessorCount() {
        return Runtime.getRuntime().availableProcessors();
    }

    public static String getStackTrace() {
        StringBuilder sb = new StringBuilder();
        for (StackTraceElement element : Thread.currentThread().getStackTrace()) {
            sb.append(element.toString()).append(NEW_LINE);
        }
        return sb.toString();
    }

    public static String getOsVersion() {
        return System.getProperty("os.version");
    }

    public static String getMachineName() {
        return System.getenv().getOrDefault("COMPUTERNAME", System.getenv().getOrDefault("HOSTNAME", "localhost"));
    }

    public static String getUserName() {
        return System.getProperty("user.name");
    }

    public static String getUserDomainName() {
        return System.getenv().getOrDefault("USERDOMAIN", "");
    }

    public static String getCurrentDirectory() {
        return System.getProperty("user.dir");
    }

    public static void exit(int exitCode) {
        System.exit(exitCode);
    }
}
