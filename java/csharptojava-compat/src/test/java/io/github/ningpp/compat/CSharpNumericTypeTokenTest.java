package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

class CSharpNumericTypeTokenTest {

    @Test
    void csharpNumericMarkersPreserveDistinctRuntimeTypeIdentity() {
        Class<?> byteType = CSharpByte.class;
        Class<?> intType = int.class;

        assertSame(CSharpByte.class, byteType);
        assertNotEquals(intType, byteType);
        assertNotEquals(CSharpByte.class, CSharpSByte.class);
        assertNotEquals(CSharpUInt16.class, CSharpUInt32.class);
        assertNotEquals(CSharpUInt32.class, CSharpUInt64.class);
    }
}
