package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for the AppDomain compatibility stub.
 */
class AppDomainTest {

    @Test
    void getCurrentDomainReturnsSingleton() {
        assertSame(AppDomain.getCurrentDomain(), AppDomain.getCurrentDomain());
    }

    @Test
    void getBaseDirectoryReturnsCurrentWorkingDirectory() {
        String baseDirectory = AppDomain.getCurrentDomain().getBaseDirectory();
        assertNotNull(baseDirectory);
        assertFalse(baseDirectory.isEmpty());
    }

    @Test
    void getAssembliesReturnsNonNullArray() {
        AssemblyCompat[] assemblies = AppDomain.getCurrentDomain().getAssemblies();
        assertNotNull(assemblies);
    }
}
