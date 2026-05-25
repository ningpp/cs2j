package Microsoft.VisualStudio.TestTools.UnitTesting;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.net.URL;
import java.nio.file.FileVisitResult;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.SimpleFileVisitor;
import java.nio.file.StandardCopyOption;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.Enumeration;
import java.util.HashSet;
import java.util.Set;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;

import org.junit.jupiter.api.extension.BeforeAllCallback;
import org.junit.jupiter.api.extension.BeforeEachCallback;
import org.junit.jupiter.api.extension.ExtensionContext;
import org.junit.jupiter.api.extension.ParameterContext;
import org.junit.jupiter.api.extension.ParameterResolutionException;
import org.junit.jupiter.api.extension.ParameterResolver;
import org.junit.jupiter.api.extension.TestInstancePostProcessor;

/**
 * JUnit 5 extension that provides MSTest-compatible runtime services:
 * - Creates a {@link TestContext} with proper test run and deployment directories.
 * - Copies deployment items declared via {@link MSTestDeploymentItem} annotations.
 * - Injects {@link TestContext} into test instance properties and lifecycle parameters.
 */
public class MSTestExtension
        implements BeforeAllCallback, BeforeEachCallback, ParameterResolver, TestInstancePostProcessor {

    private static final Set<String> deployedClassKeys = new HashSet<>();

    @Override
    public void beforeAll(ExtensionContext context) throws Exception {
        TestContext testContext = TestContext.create();
        TestContext.setCurrent(testContext);

        // Process class-level deployment items once per class
        String classKey = context.getRequiredTestClass().getName() + "#class";
        if (deployedClassKeys.add(classKey)) {
            MSTestDeploymentItem[] classItems = getDeploymentItems(context.getRequiredTestClass());
            for (MSTestDeploymentItem item : classItems) {
                deployItem(testContext, item);
            }
        }
    }

    @Override
    public void beforeEach(ExtensionContext context) throws Exception {
        TestContext testContext = TestContext.current();
        if (testContext == null) {
            testContext = TestContext.create();
            TestContext.setCurrent(testContext);
        }

        // Process method-level deployment items
        var method = context.getRequiredTestMethod();
        MSTestDeploymentItem[] methodItems = method.getAnnotationsByType(MSTestDeploymentItem.class);
        for (MSTestDeploymentItem item : methodItems) {
            deployItem(testContext, item);
        }
    }

    @Override
    public void postProcessTestInstance(Object testInstance, ExtensionContext context) throws Exception {
        TestContext testContext = TestContext.current();
        if (testContext == null) {
            testContext = TestContext.create();
            TestContext.setCurrent(testContext);
        }

        // Inject TestContext into instance properties named like testContext / testContextInstance
        injectTestContextProperties(testInstance, testContext);
    }

    @Override
    public boolean supportsParameter(ParameterContext parameterContext, ExtensionContext extensionContext)
            throws ParameterResolutionException {
        return parameterContext.getParameter().getType() == TestContext.class;
    }

    @Override
    public Object resolveParameter(ParameterContext parameterContext, ExtensionContext extensionContext)
            throws ParameterResolutionException {
        TestContext testContext = TestContext.current();
        if (testContext == null) {
            testContext = TestContext.create();
            TestContext.setCurrent(testContext);
        }
        return testContext;
    }

    private static MSTestDeploymentItem[] getDeploymentItems(Class<?> testClass) {
        MSTestDeploymentItem[] items = testClass.getAnnotationsByType(MSTestDeploymentItem.class);
        if (items.length == 0) {
            // Also check for container annotation (non-repeated form)
            MSTestDeploymentItems container = testClass.getAnnotation(MSTestDeploymentItems.class);
            if (container != null) {
                items = container.value();
            }
        }
        return items;
    }

    private static void deployItem(TestContext testContext, MSTestDeploymentItem item) throws IOException {
        Path deploymentDir = Paths.get(testContext.getDeploymentDirectory());
        Path targetDir = item.outputDirectory().isEmpty()
                ? deploymentDir
                : deploymentDir.resolve(item.outputDirectory());

        Files.createDirectories(targetDir);

        // Resolve source: try classpath resources first, then filesystem
        String source = item.source().replace('\\', '/');
        Path sourcePath = resolveSource(source);

        if (sourcePath == null) {
            System.err.println("[MSTestExtension] WARNING: Deployment source not found: " + source);
            return;
        }

        if (Files.isDirectory(sourcePath)) {
            copyDirectory(sourcePath, targetDir);
        } else {
            Path fileName = sourcePath.getFileName();
            Files.copy(sourcePath, targetDir.resolve(fileName.toString()),
                    StandardCopyOption.REPLACE_EXISTING);
        }
    }

    /**
     * Resolves a deployment source path. Tries classpath resources first,
     * then the current working directory.
     */
    private static Path resolveSource(String source) throws IOException {
        // Try classpath resource as directory listing
        ClassLoader cl = Thread.currentThread().getContextClassLoader();
        if (cl == null) {
            cl = MSTestExtension.class.getClassLoader();
        }

        // Try as a filesystem path first (for IDE test runs)
        Path fsPath = Paths.get(source);
        if (Files.exists(fsPath)) {
            return fsPath.toAbsolutePath().normalize();
        }

        // Try as a classpath resource (for Maven test runs where resources are in target/test-classes)
        URL resourceUrl = cl.getResource(source);
        if (resourceUrl != null) {
            try {
                Path resolved = Paths.get(resourceUrl.toURI());
                if (Files.exists(resolved)) {
                    return resolved;
                }
            } catch (Exception ignored) {
                // Fall through
            }
        }

        // Try relative to user.dir
        Path userDirPath = Paths.get(System.getProperty("user.dir"), source);
        if (Files.exists(userDirPath)) {
            return userDirPath.toAbsolutePath().normalize();
        }

        return null;
    }

    private static void copyDirectory(Path source, Path target) throws IOException {
        Files.walkFileTree(source, new SimpleFileVisitor<>() {
            @Override
            public FileVisitResult preVisitDirectory(Path dir, BasicFileAttributes attrs) throws IOException {
                Path relative = source.relativize(dir);
                Files.createDirectories(target.resolve(relative));
                return FileVisitResult.CONTINUE;
            }

            @Override
            public FileVisitResult visitFile(Path file, BasicFileAttributes attrs) throws IOException {
                Path relative = source.relativize(file);
                Files.copy(file, target.resolve(relative), StandardCopyOption.REPLACE_EXISTING);
                return FileVisitResult.CONTINUE;
            }
        });
    }

    private static void injectTestContextProperties(Object testInstance, TestContext testContext) {
        Class<?> clazz = testInstance.getClass();
        while (clazz != null && clazz != Object.class) {
            for (var field : clazz.getDeclaredFields()) {
                if (field.getType() == TestContext.class) {
                    try {
                        field.setAccessible(true);
                        if (field.get(testInstance) == null) {
                            field.set(testInstance, testContext);
                        }
                    } catch (Exception ignored) {
                        // Skip inaccessible fields
                    }
                }
            }
            clazz = clazz.getSuperclass();
        }
    }
}
