package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpComparerTest {

    // ---- CSharpDefaultComparer ----

    @Test
    void defaultComparer_isSingleton() {
        CSharpDefaultComparer c1 = CSharpDefaultComparer.getDefault();
        CSharpDefaultComparer c2 = CSharpDefaultComparer.getDefault();
        assertSame(c1, c2);
    }

    @Test
    void defaultComparer_compare_strings() {
        CSharpDefaultComparer c = CSharpDefaultComparer.getDefault();
        assertTrue(c.compare("apple", "banana") < 0);
        assertTrue(c.compare("banana", "apple") > 0);
        assertEquals(0, c.compare("same", "same"));
    }

    @Test
    void defaultComparer_compare_integers() {
        CSharpDefaultComparer c = CSharpDefaultComparer.getDefault();
        assertTrue(c.compare(1, 2) < 0);
        assertTrue(c.compare(2, 1) > 0);
        assertEquals(0, c.compare(5, 5));
    }

    @Test
    void defaultComparer_compare_nulls() {
        CSharpDefaultComparer c = CSharpDefaultComparer.getDefault();
        assertEquals(0, c.compare(null, null));
        assertTrue(c.compare(null, "a") < 0);
        assertTrue(c.compare("a", null) > 0);
    }

    // ---- CSharpCaseInsensitiveComparer ----

    @Test
    void caseInsensitiveComparer_isSingleton() {
        CSharpCaseInsensitiveComparer c1 = CSharpCaseInsensitiveComparer.getDefault();
        CSharpCaseInsensitiveComparer c2 = CSharpCaseInsensitiveComparer.getDefault();
        assertSame(c1, c2);
    }

    @Test
    void caseInsensitiveComparer_stringsCaseInsensitive() {
        CSharpCaseInsensitiveComparer c = CSharpCaseInsensitiveComparer.getDefault();
        assertEquals(0, c.compare("Hello", "hello"));
        assertEquals(0, c.compare("ABC", "abc"));
        assertTrue(c.compare("apple", "Banana") < 0);
    }

    // ---- CSharpStructuralComparisons ----

    @Test
    void structuralComparer_isNonNull() {
        CSharpComparer comparer = CSharpStructuralComparisons.getStructuralComparer();
        assertNotNull(comparer);
    }

    @Test
    void structuralEqualityComparer_isNonNull() {
        CSharpEqualityComparer eq = CSharpStructuralComparisons.getStructuralEqualityComparer();
        assertNotNull(eq);
    }

    @Test
    void structuralComparer_compareArrays() {
        CSharpComparer c = CSharpStructuralComparisons.getStructuralComparer();
        Object[] arr1 = {1, 2, 3};
        Object[] arr2 = {1, 2, 3};
        Object[] arr3 = {1, 2, 4};
        assertEquals(0, c.compare(arr1, arr2));
        assertTrue(c.compare(arr1, arr3) < 0);
    }

    @Test
    void structuralEqualityComparer_arraysEqual() {
        CSharpEqualityComparer eq = CSharpStructuralComparisons.getStructuralEqualityComparer();
        Object[] arr1 = {1, 2, 3};
        Object[] arr2 = {1, 2, 3};
        Object[] arr3 = {1, 2, 4};
        assertTrue(eq.equals(arr1, arr2));
        assertFalse(eq.equals(arr1, arr3));
    }

    // ---- CSharpDefaultComparerGeneric ----

    @Test
    void defaultComparerGeneric_isSingleton() {
        CSharpDefaultComparerGeneric<String> c1 = CSharpDefaultComparerGeneric.getDefault();
        CSharpDefaultComparerGeneric<String> c2 = CSharpDefaultComparerGeneric.getDefault();
        assertSame(c1, c2);
    }

    @Test
    void defaultComparerGeneric_compare_strings() {
        CSharpDefaultComparerGeneric<String> c = CSharpDefaultComparerGeneric.getDefault();
        assertTrue(c.compare("apple", "banana") < 0);
        assertEquals(0, c.compare("same", "same"));
    }

    @Test
    void defaultComparerGeneric_compare_nulls() {
        CSharpDefaultComparerGeneric<String> c = CSharpDefaultComparerGeneric.getDefault();
        assertEquals(0, c.compare(null, null));
        assertTrue(c.compare(null, "a") < 0);
        assertTrue(c.compare("a", null) > 0);
    }

    // ---- CSharpDefaultEqualityComparerGeneric ----

    @Test
    void defaultEqualityComparerGeneric_isSingleton() {
        CSharpDefaultEqualityComparerGeneric<String> e1 = CSharpDefaultEqualityComparerGeneric.getDefault();
        CSharpDefaultEqualityComparerGeneric<String> e2 = CSharpDefaultEqualityComparerGeneric.getDefault();
        assertSame(e1, e2);
    }

    @Test
    void defaultEqualityComparerGeneric_equals() {
        CSharpDefaultEqualityComparerGeneric<String> e = CSharpDefaultEqualityComparerGeneric.getDefault();
        assertTrue(e.equals("hello", "hello"));
        assertFalse(e.equals("hello", "world"));
        assertTrue(e.equals(null, null));
        assertFalse(e.equals(null, "hello"));
    }

    @Test
    void defaultEqualityComparerGeneric_hashCode() {
        CSharpDefaultEqualityComparerGeneric<String> e = CSharpDefaultEqualityComparerGeneric.getDefault();
        assertEquals("hello".hashCode(), e.hashCode("hello"));
        assertEquals(0, e.hashCode(null));
    }

    // ---- CSharpRefEqualityComparer ----

    @Test
    void refEqualityComparer_isSingleton() {
        CSharpRefEqualityComparer e1 = CSharpRefEqualityComparer.getInstance();
        CSharpRefEqualityComparer e2 = CSharpRefEqualityComparer.getInstance();
        assertSame(e1, e2);
    }

    @Test
    void refEqualityComparer_sameReference_equals() {
        CSharpRefEqualityComparer e = CSharpRefEqualityComparer.getInstance();
        Object obj = new Object();
        assertTrue(e.equals(obj, obj));
    }

    @Test
    void refEqualityComparer_differentReference_notEquals() {
        CSharpRefEqualityComparer e = CSharpRefEqualityComparer.getInstance();
        Object obj1 = new Object();
        Object obj2 = new Object();
        assertFalse(e.equals(obj1, obj2));
    }

    @Test
    void refEqualityComparer_hashCode_isIdentityHashCode() {
        CSharpRefEqualityComparer e = CSharpRefEqualityComparer.getInstance();
        Object obj = new Object();
        assertEquals(System.identityHashCode(obj), e.hashCode(obj));
    }
}
