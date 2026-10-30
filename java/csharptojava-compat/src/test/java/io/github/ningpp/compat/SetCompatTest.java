package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Regression tests for the C# {@code ISet<T>} relational bridges. Converted
 * receivers are {@link CSharpHashSet}/{@link CSharpISet} values, which expose
 * {@code contains}/{@code size} but do not implement {@link java.util.Set};
 * second arguments stay plain {@link Iterable}s (including non-set ones).
 */
class SetCompatTest {

    @Test
    void isSubsetOf_cSharpHashSetReceiver() {
        CSharpHashSet<String> expected = new CSharpHashSet<>(List.of("a", "b"));
        assertTrue(SetCompat.isSubsetOf(expected, List.of("a", "b", "c")));
        assertTrue(SetCompat.isSubsetOf(expected, List.of("b", "a")));
        assertTrue(SetCompat.isSubsetOf(expected, expected));
        assertTrue(SetCompat.isSubsetOf(new CSharpHashSet<String>(), List.of("a")));
        assertFalse(SetCompat.isSubsetOf(expected, List.of("a")));
        assertFalse(SetCompat.isSubsetOf(expected, List.of("c", "d")));
    }

    @Test
    void isSubsetOf_acceptsNonSetArrayListSecondArgument() {
        CSharpHashSet<String> expected = new CSharpHashSet<>(List.of("a", "b"));
        ArrayList<String> other = new ArrayList<>(List.of("b", "a", "c"));
        assertTrue(SetCompat.isSubsetOf(expected, other));
        other.remove("b");
        assertFalse(SetCompat.isSubsetOf(expected, other));
    }

    @Test
    void isSupersetOf_cSharpHashSetReceiver() {
        CSharpHashSet<String> set = new CSharpHashSet<>(List.of("a", "b", "c"));
        assertTrue(SetCompat.isSupersetOf(set, List.of("a", "c")));
        assertTrue(SetCompat.isSupersetOf(set, set));
        assertTrue(SetCompat.isSupersetOf(set, new ArrayList<String>()));
        assertFalse(SetCompat.isSupersetOf(set, List.of("a", "z")));
    }

    @Test
    void setEquals_cSharpHashSetReceiver() {
        CSharpHashSet<String> set = new CSharpHashSet<>(List.of("a", "b"));
        assertTrue(SetCompat.setEquals(set, List.of("b", "a")));
        assertTrue(SetCompat.setEquals(set, new ArrayList<>(List.of("a", "b"))));
        assertTrue(SetCompat.setEquals(set, set));
        assertFalse(SetCompat.setEquals(set, List.of("a", "b", "c")));
        assertFalse(SetCompat.setEquals(set, List.of("a", "c")));
        assertFalse(SetCompat.setEquals(set, List.of("a", "b", "b")));
    }

    @Test
    void overlaps_cSharpHashSetReceiver() {
        CSharpHashSet<String> set = new CSharpHashSet<>(List.of("a", "b"));
        assertTrue(SetCompat.overlaps(set, List.of("c", "b")));
        assertTrue(SetCompat.overlaps(set, new ArrayList<>(List.of("a"))));
        assertFalse(SetCompat.overlaps(set, List.of("c", "d")));
        assertFalse(SetCompat.overlaps(set, new ArrayList<String>()));
    }

    @Test
    void bridges_acceptCSharpISetReceiver() {
        CSharpISet<String> set = new CSharpHashSet<>(List.of("a", "b"));
        assertTrue(SetCompat.isSubsetOf(set, List.of("a", "b", "c")));
        assertTrue(SetCompat.isSupersetOf(set, List.of("a")));
        assertTrue(SetCompat.setEquals(set, List.of("b", "a")));
        assertTrue(SetCompat.overlaps(set, List.of("b")));
    }

    @Test
    void bridges_acceptCSharpSortedSetReceiver() {
        CSharpSortedSet<String> set = new CSharpSortedSet<>(List.of("a", "b"));
        assertTrue(SetCompat.isSubsetOf(set, List.of("a", "b")));
        assertFalse(SetCompat.isSubsetOf(set, List.of("a")));
        assertTrue(SetCompat.isSupersetOf(set, List.of("a")));
        assertTrue(SetCompat.setEquals(set, List.of("a", "b")));
        assertTrue(SetCompat.overlaps(set, List.of("b")));
    }

    @Test
    void nullReceiver_throwsNullPointerException() {
        assertThrows(NullPointerException.class, () -> SetCompat.isSubsetOf(null, List.of("a")));
        assertThrows(NullPointerException.class, () -> SetCompat.isSupersetOf(null, List.of("a")));
        assertThrows(NullPointerException.class, () -> SetCompat.setEquals(null, List.of("a")));
        assertThrows(NullPointerException.class, () -> SetCompat.overlaps(null, List.of("a")));
    }
}
