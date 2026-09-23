package io.github.ningpp.compat;

import io.github.ningpp.compat.assemblyfixtures.SubpackageFixture;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * AssemblyCompat models C# {@code Assembly} identity for classes without a registered
 * compat identity: {@code FullName} must START WITH the assembly's name (a C# FullName is
 * "Name, Version=..., Culture=neutral, PublicKeyToken=..." — never a file URL), and
 * {@code Location} carries the same identity in path form so AssemblyResolutionTests'
 * "assembly resolved under the test output directory" check translates into
 * namespace space (a sub-namespace assembly sits under the root assembly's directory).
 */
class AssemblyCompatAssemblyIdentityTest {

    @Test
    void fullNameStartsWithAssemblyName_notUrl() {
        AssemblyCompat asm = TypeHelper.getAssembly(JavaScriptEncoder.class);
        assertTrue(asm.getFullName().startsWith("io.github.ningpp.compat"),
            () -> "actual fullName: " + asm.getFullName());
        assertFalse(asm.getFullName().contains("file:"),
            () -> "actual fullName: " + asm.getFullName());
    }

    @Test
    void locationIsNotEmpty() {
        assertFalse(TypeHelper.getAssembly(JavaScriptEncoder.class).getLocation().isEmpty());
    }

    @Test
    void subpackageAssemblyLocationSitsUnderParentAssemblyDirectory() {
        AssemblyCompat root = TypeHelper.getAssembly(JavaScriptEncoder.class);
        AssemblyCompat sub = TypeHelper.getAssembly(SubpackageFixture.ANCHOR);
        String rootDirectory = PathHelper.getDirectoryName(sub.getLocation());
        assertTrue(StringHelper.startsWith(root.getLocation(), rootDirectory, true),
            () -> root.getLocation() + " not under " + rootDirectory);
    }
}
