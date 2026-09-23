package io.github.ningpp.compat.assemblyfixtures;

/**
 * Anchor type standing in for a "sub-assembly" whose namespace sits under
 * {@code io.github.ningpp.compat} — AssemblyCompatAssemblyIdentityTest checks that its
 * assembly Location resolves into the root assembly's directory, mirroring how
 * AssemblyResolutionTests expects the locally built System.Text.Json to sit next to the
 * test assembly.
 */
public final class SubpackageFixture {

    /** Class anchor usable without reflection. */
    public static final Class<SubpackageFixture> ANCHOR = SubpackageFixture.class;

    private SubpackageFixture() {
    }
}
