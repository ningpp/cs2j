package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

import org.junit.jupiter.api.Test;

/// Generated code compares AssemblyCompat values with == (e.g.
/// JsonConverter1.IsInternalConverter). fromClass must therefore return a cached
/// singleton for the same class loader + class.
class AssemblyCompatEqualityTest {
    @Test
    void fromClassReturnsStableInstance() {
        AssemblyCompat a = TypeHelper.getAssembly(String.class);
        AssemblyCompat b = TypeHelper.getAssembly(String.class);

        assertSame(a, b);
        assertEquals(a, b);
        assertEquals(a.hashCode(), b.hashCode());
        assertSame(a, TypeHelper.getAssembly(Integer.class));
    }
}
