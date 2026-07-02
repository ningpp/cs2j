package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpBitArrayTest {

    // ---- Construction ----

    @Test
    void constructorWithLength_createsAllFalse() {
        CSharpBitArray ba = new CSharpBitArray(5);
        assertEquals(5, ba.getCount());
        assertEquals(5, ba.getLength());
        for (int i = 0; i < 5; i++) {
            assertFalse(ba.get(i));
        }
    }

    @Test
    void constructorWithLengthAndDefault_true() {
        CSharpBitArray ba = new CSharpBitArray(3, true);
        for (int i = 0; i < 3; i++) {
            assertTrue(ba.get(i));
        }
    }

    @Test
    void constructorWithLengthAndDefault_false() {
        CSharpBitArray ba = new CSharpBitArray(3, false);
        for (int i = 0; i < 3; i++) {
            assertFalse(ba.get(i));
        }
    }

    @Test
    void constructorWithBooleanArray() {
        CSharpBitArray ba = new CSharpBitArray(new boolean[]{true, false, true});
        assertEquals(3, ba.getLength());
        assertTrue(ba.get(0));
        assertFalse(ba.get(1));
        assertTrue(ba.get(2));
    }

    @Test
    void constructorWithNegativeLength_throws() {
        assertThrows(IndexOutOfBoundsException.class, () -> new CSharpBitArray(-1));
    }

    // ---- Get / Set ----

    @Test
    void set_andGet() {
        CSharpBitArray ba = new CSharpBitArray(5);
        ba.set(0, true);
        ba.set(2, true);
        ba.set(4, true);
        assertTrue(ba.get(0));
        assertFalse(ba.get(1));
        assertTrue(ba.get(2));
        assertFalse(ba.get(3));
        assertTrue(ba.get(4));
    }

    @Test
    void setAll_true() {
        CSharpBitArray ba = new CSharpBitArray(3);
        ba.setAll(true);
        for (int i = 0; i < 3; i++) assertTrue(ba.get(i));
    }

    @Test
    void setAll_false() {
        CSharpBitArray ba = new CSharpBitArray(3, true);
        ba.setAll(false);
        for (int i = 0; i < 3; i++) assertFalse(ba.get(i));
    }

    // ---- Length ----

    @Test
    void setLength_changesLength() {
        CSharpBitArray ba = new CSharpBitArray(3);
        ba.setLength(5);
        assertEquals(5, ba.getLength());
    }

    @Test
    void setLength_negative_throws() {
        CSharpBitArray ba = new CSharpBitArray(3);
        assertThrows(IndexOutOfBoundsException.class, () -> ba.setLength(-1));
    }

    // ---- And ----

    @Test
    void and_performsBitwiseAnd() {
        CSharpBitArray ba1 = new CSharpBitArray(new boolean[]{true, false, true, false, true});
        CSharpBitArray ba2 = new CSharpBitArray(new boolean[]{true, true, false, false, true});
        CSharpBitArray result = ba1.and(ba2);
        assertSame(ba1, result);
        assertTrue(ba1.get(0));
        assertFalse(ba1.get(1));
        assertFalse(ba1.get(2));
        assertFalse(ba1.get(3));
        assertTrue(ba1.get(4));
    }

    // ---- Or ----

    @Test
    void or_performsBitwiseOr() {
        CSharpBitArray ba1 = new CSharpBitArray(new boolean[]{true, false, true});
        CSharpBitArray ba2 = new CSharpBitArray(new boolean[]{false, true, false});
        ba1.or(ba2);
        assertTrue(ba1.get(0));
        assertTrue(ba1.get(1));
        assertTrue(ba1.get(2));
    }

    // ---- Xor ----

    @Test
    void xor_performsBitwiseXor() {
        CSharpBitArray ba1 = new CSharpBitArray(new boolean[]{true, true, false});
        CSharpBitArray ba2 = new CSharpBitArray(new boolean[]{true, false, false});
        ba1.xor(ba2);
        assertFalse(ba1.get(0));
        assertTrue(ba1.get(1));
        assertFalse(ba1.get(2));
    }

    // ---- Not ----

    @Test
    void not_flipsAllBits() {
        CSharpBitArray ba = new CSharpBitArray(new boolean[]{true, false, true});
        ba.not();
        assertFalse(ba.get(0));
        assertTrue(ba.get(1));
        assertFalse(ba.get(2));
    }

    // ---- Clone ----

    @Test
    void clone_createsIndependentCopy() {
        CSharpBitArray ba = new CSharpBitArray(new boolean[]{true, false});
        CSharpBitArray cloned = ba.clone();
        assertEquals(ba.getLength(), cloned.getLength());
        assertEquals(ba.get(0), cloned.get(0));
        // Modifying clone should not affect original
        cloned.set(0, false);
        assertTrue(ba.get(0));
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesBooleans() {
        CSharpBitArray ba = new CSharpBitArray(new boolean[]{true, false, true});
        Object[] arr = new Object[5];
        ba.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals(true, arr[1]);
        assertEquals(false, arr[2]);
        assertEquals(true, arr[3]);
    }

    // ---- IsReadOnly ----

    @Test
    void isReadOnly_returnsFalse() {
        CSharpBitArray ba = new CSharpBitArray(3);
        assertFalse(ba.getIsReadOnly());
    }

    // ---- Oracle data validation (BitArray) ----

    @Test
    void oracleData_bitArrayOperations() {
        // From C# test data: new BitArray(5) -> all false
        CSharpBitArray ba = new CSharpBitArray(5);
        // Set bits 0, 2, 4 to true
        ba.set(0, true);
        ba.set(2, true);
        ba.set(4, true);
        assertTrue(ba.get(0));
        assertFalse(ba.get(1));
        assertEquals(5, ba.getCount());
        assertEquals(5, ba.getLength());
        // And with [true,true,false,false,true]
        CSharpBitArray other = new CSharpBitArray(new boolean[]{true, true, false, false, true});
        ba.and(other);
        // Result: [true,false,false,false,true]
        assertTrue(ba.get(0));
        assertFalse(ba.get(1));
        assertFalse(ba.get(2));
        assertFalse(ba.get(3));
        assertTrue(ba.get(4));
        // Not
        ba.not();
        assertFalse(ba.get(0));
        assertTrue(ba.get(1));
        assertTrue(ba.get(2));
        assertTrue(ba.get(3));
        assertFalse(ba.get(4));
    }
}
