package Microsoft.VisualStudio.TestTools.UnitTesting;

public class TestContext {
    public static final String TestDir = System.getProperty("user.dir");
    public static final String DeploymentDirectory = System.getProperty("user.dir");
    public static final String TestRunDirectory = System.getProperty("user.dir");

    public String getDeploymentDirectory() {
        return DeploymentDirectory;
    }

    public String getTestRunDirectory() {
        return TestRunDirectory;
    }

    public String getTestDir() {
        return TestDir;
    }

    public static void writeLine(String line) {
        System.out.println(line);
    }

    public static void writeLine(String format, Object... args) {
        System.out.println(String.format(format, args));
    }
}
