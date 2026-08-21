package io.github.ningpp.compat;

import java.net.URL;
import java.nio.file.Paths;

public final class AppContext {
    private AppContext() {}

    public static String getBaseDirectory() {
        // Try to find the classpath root that contains resources, similar to
        // C#'s AppContext.BaseDirectory which returns the directory of the running assembly.
        // In Maven, resources are copied to target/test-classes or target/classes.
        try {
            URL resource = Thread.currentThread().getContextClassLoader()
                    .getResource(".");
            if (resource != null) {
                // Convert URL to a proper filesystem path via Paths to handle
                // platform-specific path separators and drive letters correctly.
                return Paths.get(resource.toURI()).toString();
            }
        } catch (Exception e) {
            // fall through
        }
        return System.getProperty("user.dir");
    }
}
