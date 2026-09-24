package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.lang.foreign.MemorySegment;

import static org.junit.jupiter.api.Assertions.*;

class GCHandleTest {

    // ---- alloc ----

    @Test
    void alloc_returnsAllocatedHandle() {
        GCHandle handle = GCHandle.alloc(new char[] { 'H', 'e', 'l', 'l', 'o' });
        assertNotNull(handle);
        assertTrue(handle.isAllocated());
    }

    @Test
    void alloc_withType_pinned() {
        GCHandle handle = GCHandle.alloc(new byte[] { 1, 2, 3, 4 }, GCHandleType.Pinned);
        assertTrue(handle.isAllocated());
    }

    @Test
    void alloc_withType_normal() {
        GCHandle handle = GCHandle.alloc(new Object(), GCHandleType.Normal);
        assertTrue(handle.isAllocated());
    }

    @Test
    void alloc_null_throws() {
        assertThrows(ArgumentNullException.class, () -> GCHandle.alloc(null));
    }

    @Test
    void alloc_withType_null_throws() {
        assertThrows(ArgumentNullException.class, () -> GCHandle.alloc(null, GCHandleType.Pinned));
    }

    // ---- addrOfPinnedObject ----

    @Test
    void addrOfPinnedObject_charArray() {
        char[] arr = new char[] { 'x', 'y' };
        GCHandle handle = GCHandle.alloc(arr);
        MemorySegment addr = handle.addrOfPinnedObject();
        assertNotNull(addr);
        assertEquals(arr.length * 2L, addr.byteSize());
    }

    @Test
    void addrOfPinnedObject_byteArray() {
        byte[] arr = new byte[] { 1, 2, 3, 4 };
        MemorySegment addr = GCHandle.alloc(arr).addrOfPinnedObject();
        assertEquals(arr.length, addr.byteSize());
    }

    @Test
    void addrOfPinnedObject_intArray() {
        int[] arr = new int[] { 10, 20, 30 };
        MemorySegment addr = GCHandle.alloc(arr).addrOfPinnedObject();
        assertEquals(arr.length * 4L, addr.byteSize());
    }

    @Test
    void addrOfPinnedObject_longArray() {
        long[] arr = new long[] { 100L, 200L };
        MemorySegment addr = GCHandle.alloc(arr).addrOfPinnedObject();
        assertEquals(arr.length * 8L, addr.byteSize());
    }

    @Test
    void addrOfPinnedObject_doubleArray() {
        double[] arr = new double[] { 1.5, 2.5 };
        MemorySegment addr = GCHandle.alloc(arr).addrOfPinnedObject();
        assertEquals(arr.length * 8L, addr.byteSize());
    }

    @Test
    void addrOfPinnedObject_floatArray() {
        float[] arr = new float[] { 1.0f, 2.0f, 3.0f };
        MemorySegment addr = GCHandle.alloc(arr).addrOfPinnedObject();
        assertEquals(arr.length * 4L, addr.byteSize());
    }

    @Test
    void addrOfPinnedObject_shortArray() {
        short[] arr = new short[] { 100, 200 };
        MemorySegment addr = GCHandle.alloc(arr).addrOfPinnedObject();
        assertEquals(arr.length * 2L, addr.byteSize());
    }

    @Test
    void addrOfPinnedObject_memorySegment_returnsSame() {
        char[] arr = new char[] { 'a', 'b' };
        MemorySegment original = MemorySegment.ofArray(arr);
        assertSame(original, GCHandle.alloc(original).addrOfPinnedObject());
    }

    @Test
    void addrOfPinnedObject_nonArray_returnsNull() {
        assertNull(GCHandle.alloc("not an array").addrOfPinnedObject());
    }

    @Test
    void addrOfPinnedObject_reflectsTargetSetter() {
        GCHandle handle = GCHandle.alloc(new char[] { 'a' });
        byte[] replacement = new byte[] { 7, 8, 9 };
        handle.setTarget(replacement);
        assertEquals(replacement.length, handle.addrOfPinnedObject().byteSize());
    }

    // ---- target ----

    @Test
    void getTarget_returnsAllocatedValue() {
        Object value = new Object();
        assertSame(value, GCHandle.alloc(value).getTarget());
    }

    // ---- isAllocated ----

    @Test
    void isAllocated_allocatedHandle_returnsTrue() {
        assertTrue(GCHandle.alloc(new char[] { 'a' }).isAllocated());
    }

    @Test
    void isAllocated_afterFree_returnsFalse() {
        GCHandle handle = GCHandle.alloc(new char[] { 'a' });
        handle.free();
        assertFalse(handle.isAllocated());
    }

    // ---- free ----

    @Test
    void free_returnsTrueWhenAllocated() {
        assertTrue(GCHandle.alloc(new char[] { 'a' }).free());
    }

    @Test
    void free_releasesTarget() {
        GCHandle handle = GCHandle.alloc(new char[] { 'a' });
        handle.free();
        assertNull(handle.getTarget());
    }

    // ---- static helpers (object-typed receivers) ----

    @Test
    void static_isAllocated_instanceDelegates() {
        assertTrue(GCHandle.isAllocated(GCHandle.alloc(new char[] { 'a' })));
    }

    @Test
    void static_isAllocated_nonNull_returnsTrue() {
        assertTrue(GCHandle.isAllocated(new Object()));
    }

    @Test
    void static_isAllocated_null_returnsFalse() {
        assertFalse(GCHandle.isAllocated(null));
    }

    @Test
    void static_addrOfPinnedObject_withInstance() {
        char[] arr = new char[] { 'x', 'y' };
        MemorySegment addr = GCHandle.addrOfPinnedObject(GCHandle.alloc(arr));
        assertNotNull(addr);
        assertEquals(arr.length * 2L, addr.byteSize());
    }

    @Test
    void static_addrOfPinnedObject_null_returnsNull() {
        assertNull(GCHandle.addrOfPinnedObject(null));
    }

    @Test
    void static_free_withInstance_returnsTrue() {
        GCHandle handle = GCHandle.alloc(new char[] { 'a' });
        assertTrue(GCHandle.free(handle));
        assertFalse(handle.isAllocated());
    }

    @Test
    void static_free_null_returnsFalse() {
        assertFalse(GCHandle.free(null));
    }

    // ---- GCHandleType ----

    @Test
    void gcHandleType_valuesMirrorDotNet() {
        assertEquals(0, GCHandleType.Weak.getValue());
        assertEquals(1, GCHandleType.WeakTrackResurrection.getValue());
        assertEquals(2, GCHandleType.Normal.getValue());
        assertEquals(3, GCHandleType.Pinned.getValue());
    }

    @Test
    void gcHandleType_fromValue_roundTrips() {
        assertSame(GCHandleType.Weak, GCHandleType.fromValue(0));
        assertSame(GCHandleType.WeakTrackResurrection, GCHandleType.fromValue(1));
        assertSame(GCHandleType.Normal, GCHandleType.fromValue(2));
        assertSame(GCHandleType.Pinned, GCHandleType.fromValue(3));
    }

    @Test
    void gcHandleType_fromValue_unknown_throws() {
        assertThrows(IllegalArgumentException.class, () -> GCHandleType.fromValue(4));
    }
}
