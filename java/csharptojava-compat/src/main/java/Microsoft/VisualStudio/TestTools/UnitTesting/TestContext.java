package Microsoft.VisualStudio.TestTools.UnitTesting;

import java.nio.file.Paths;

/**
 * MSTest-compatible TestContext with proper test run and deployment directories.
 *
 * <p>Directory layout (configurable via system properties):
 * <pre>
 *   cs2j.testRunDirectory (default: ${user.dir}/target/cs2j-test-run)
 *     Deployment/      ← cs2j.deploymentDirectory
 *     Out/             ← TestRunDirectory/Out
 * </pre>
 *
 * <p>Static fields {@code TestDir}, {@code DeploymentDirectory}, {@code TestRunDirectory}
 * are maintained for backward compatibility and reflect the current context values.
 */
public class TestContext {

    private static final ThreadLocal<TestContext> currentContext = new ThreadLocal<>();

    // Backward-compatible static fields — updated when a TestContext is created.
    public static String TestDir;
    public static String DeploymentDirectory;
    public static String TestRunDirectory;

    static {
        String defaultRunDir = resolveDefaultRunDirectory();
        TestDir = defaultRunDir;
        TestRunDirectory = defaultRunDir;
        DeploymentDirectory = Paths.get(defaultRunDir, "Deployment").toString();
    }

    private final String testRunDirectory;
    private final String deploymentDirectory;
    private final String outDirectory;

    private TestContext() {
        this.testRunDirectory = System.getProperty("cs2j.testRunDirectory",
                resolveDefaultRunDirectory());
        this.deploymentDirectory = System.getProperty("cs2j.deploymentDirectory",
                Paths.get(testRunDirectory, "Deployment").toString());
        this.outDirectory = Paths.get(testRunDirectory, "Out").toString();

        // Update static fields for backward compatibility
        TestDir = testRunDirectory;
        TestRunDirectory = testRunDirectory;
        DeploymentDirectory = deploymentDirectory;
    }

    private static String resolveDefaultRunDirectory() {
        return Paths.get(System.getProperty("user.dir"), "target", "cs2j-test-run").toString();
    }

    /** Creates a new TestContext and sets it as current. */
    public static TestContext create() {
        TestContext ctx = new TestContext();
        currentContext.set(ctx);
        return ctx;
    }

    /** Returns the current TestContext for this thread, or {@code null}. */
    public static TestContext current() {
        return currentContext.get();
    }

    /** Sets the current TestContext for this thread. */
    public static void setCurrent(TestContext ctx) {
        currentContext.set(ctx);
        if (ctx != null) {
            TestDir = ctx.testRunDirectory;
            TestRunDirectory = ctx.testRunDirectory;
            DeploymentDirectory = ctx.deploymentDirectory;
        }
    }

    public String getDeploymentDirectory() {
        return deploymentDirectory;
    }

    public String getTestRunDirectory() {
        return testRunDirectory;
    }

    public String getTestDir() {
        return testRunDirectory;
    }

    /** Directory where Out files are placed (TestRunDirectory/Out). */
    public String getOutDirectory() {
        return outDirectory;
    }

    public void writeLine(String line) {
        System.out.println(line);
    }

    public void writeLine(String format, Object... args) {
        System.out.println(String.format(format, args));
    }
}
