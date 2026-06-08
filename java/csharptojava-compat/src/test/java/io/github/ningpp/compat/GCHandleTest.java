package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.lang.foreign.MemorySegment;

import static org.junit.jupiter.api.Assertions.*;

class GCHandleTest {

    // ---- alloc ----

    @Test
    void alloc_charArray() {
        char[] arr = new char[] { 'H', 'e', 'l', 'l', 'o' };
        MemorySegment ms = GCHandle.alloc(arr);
        assertNotNull(ms);
        assertEquals(arr.length * 2L, ms.byteSize());
    }

    @Test
    void alloc_byteArray() {
        byte[] arr = new byte[] { 1, 2, 3, 4 };
        MemorySegment ms = GCHandle.alloc(arr);
        assertNotNull(ms);
        assertEquals(arr.length, ms.byteSize());
    }

    @Test
    void alloc_intArray() {
        int[] arr = new int[] { 10, 20, 30 };
        MemorySegment ms = GCHandle.alloc(arr);
        assertNotNull(ms);
        assertEquals(arr.length * 4L, ms.byteSize());
    }

    @Test
    void alloc_longArray() {
        long[] arr = new long[] { 100L, 200L };
        MemorySegment ms = GCHandle.alloc(arr);
        assertNotNull(ms);
        assertEquals(arr.length * 8L, ms.byteSize());
    }

    @Test
    void alloc_doubleArray() {
        double[] arr = new double[] { 1.5, 2.5 };
        MemorySegment ms = GCHandle.alloc(arr);
        assertNotNull(ms);
        assertEquals(arr.length * 8L, ms.byteSize());
    }

    @Test
    void alloc_floatArray() {
        float[] arr = new float[] { 1.0f, 2.0f, 3.0f };
        MemorySegment ms = GCHandle.alloc(arr);
        assertNotNull(ms);
        assertEquals(arr.length * 4L, ms.byteSize());
    }

    @Test
    void alloc_shortArray() {
        short[] arr = new short[] { 100, 200 };
        MemorySegment ms = GCHandle.alloc(arr);
        assertNotNull(ms);
        assertEquals(arr.length * 2L, ms.byteSize());
    }

    @Test
    void alloc_memorySegment_returnsSame() {
        char[] arr = new char[] { 'a', 'b' };
        MemorySegment original = MemorySegment.ofArray(arr);
        MemorySegment result = GCHandle.alloc(original);
        assertSame(original, result);
    }

    @Test
    void alloc_unsupportedType_throws() {
        assertThrows(IllegalArgumentException.class, () -> GCHandle.alloc("not an array"));
    }

    // ---- addrOfPinnedObject ----

    @Test
    void addrOfPinnedObject_charArray() {
        char[] arr = new char[] { 'x', 'y' };
        Object handle = GCHandle.alloc(arr);
        MemorySegment addr = GCHandle.addrOfPinnedObject(handle);
        assertNotNull(addr);
    }

    @Test
    void addrOfPinnedObject_null_returnsNull() {
        assertNull(GCHandle.addrOfPinnedObject(null));
    }

    @Test
    void addrOfPinnedObject_memorySegment_returnsSame() {
        char[] arr = new char[] { 'a' };
        MemorySegment ms = MemorySegment.ofArray(arr);
        Object handle = ms;
        MemorySegment result = GCHandle.addrOfPinnedObject(handle);
        assertSame(ms, result);
    }

    // ---- isAllocated ----

    @Test
    void isAllocated_nonNull_returnsTrue() {
        assertTrue(GCHandle.isAllocated(new Object()));
    }

    @Test
    void isAllocated_null_returnsFalse() {
        assertFalse(GCHandle.isAllocated(null));
    }

    // ---- free ----

    @Test
    void free_doesNotThrow() {
        Object handle = GCHandle.alloc(new char[] { 'a' });
        assertDoesNotThrow(() -> GCHandle.free(handle));
    }

    @Test
    void free_null_doesNotThrow() {
        assertDoesNotThrow(() -> GCHandle.free(null));
    }
}
