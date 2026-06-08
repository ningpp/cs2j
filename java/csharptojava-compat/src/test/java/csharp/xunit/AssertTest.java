package csharp.xunit;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

import static org.junit.jupiter.api.Assertions.*;

class AssertTest {

    // ── True ─────────────────────────────────────────────────────────

    @Test
    void true_succeeds_whenTrue() {
        Assert.true_(true);
    }

    @Test
    void true_succeeds_withBooleanTrue() {
        Assert.true_(Boolean.TRUE);
    }

    @Test
    void true_fails_whenFalse() {
        assertThrows(AssertionError.class, () -> Assert.true_(false));
    }

    @Test
    void true_withMessage_fails() {
        AssertionError e = assertThrows(AssertionError.class,
            () -> Assert.true_(false, "custom message"));
        assertTrue(e.getMessage().contains("custom message"));
    }

    // ── False ────────────────────────────────────────────────────────

    @Test
    void false_succeeds_whenFalse() {
        Assert.false_(false);
    }

    @Test
    void false_fails_whenTrue() {
        assertThrows(AssertionError.class, () -> Assert.false_(true));
    }

    // ── Null / NotNull ───────────────────────────────────────────────

    @Test
    void null_succeeds_whenNull() {
        Assert.null_(null);
    }

    @Test
    void null_fails_whenNotNull() {
        assertThrows(AssertionError.class, () -> Assert.null_("not null"));
    }

    @Test
    void notNull_succeeds_whenNotNull() {
        Assert.notNull("hello");
    }

    @Test
    void notNull_fails_whenNull() {
        assertThrows(AssertionError.class, () -> Assert.notNull(null));
    }

    @Test
    void notNull_returnsValue() {
        String result = Assert.notNull(String.class, "value");
        assertEquals("value", result);
    }

    // ── Equal ────────────────────────────────────────────────────────

    @Test
    void equal_succeeds_whenEqual() {
        Assert.equal("hello", "hello");
        Assert.equal(42, 42);
    }

    @Test
    void equal_fails_whenNotEqual() {
        assertThrows(AssertionError.class, () -> Assert.equal("a", "b"));
    }

    @Test
    void equal_withTolerance_succeeds() {
        Assert.equal(1.0, 1.05, 0.1);
        Assert.equal(1.0f, 1.05f, 0.1f);
    }

    @Test
    void equal_withTolerance_fails() {
        assertThrows(AssertionError.class, () -> Assert.equal(1.0, 2.0, 0.1));
    }

    @Test
    void equal_withPrecision_succeeds() {
        Assert.equal(3.14159, 3.142, 2);
    }

    @Test
    void equal_withIgnoreCase_succeeds() {
        Assert.equal("Hello", "hello", true);
    }

    @Test
    void equal_withIgnoreCase_fails_whenDifferent() {
        assertThrows(AssertionError.class, () -> Assert.equal("Hello", "world", true));
    }

    @Test
    void equal_iterables_succeeds() {
        Assert.equal(Arrays.asList(1, 2, 3), Arrays.asList(1, 2, 3));
    }

    @Test
    void equal_iterables_fails_whenDifferent() {
        assertThrows(AssertionError.class,
            () -> Assert.equal(Arrays.asList(1, 2), Arrays.asList(1, 3)));
    }

    @Test
    void equal_numberTypes() {
        Assert.equal(1, 1L);
        Assert.equal(Integer.valueOf(42), Long.valueOf(42));
    }

    // ── NotEqual ─────────────────────────────────────────────────────

    @Test
    void notEqual_succeeds_whenNotEqual() {
        Assert.notEqual("a", "b");
    }

    @Test
    void notEqual_fails_whenEqual() {
        assertThrows(AssertionError.class, () -> Assert.notEqual("a", "a"));
    }

    @Test
    void notEqual_withTolerance_succeeds() {
        Assert.notEqual(1.0, 2.0, 0.1);
    }

    // ── Same / NotSame ───────────────────────────────────────────────

    @Test
    void same_succeeds_whenSameReference() {
        Object obj = new Object();
        Assert.same(obj, obj);
    }

    @Test
    void same_fails_whenDifferentReference() {
        assertThrows(AssertionError.class, () -> Assert.same(new Object(), new Object()));
    }

    @Test
    void notSame_succeeds_whenDifferentReference() {
        Assert.notSame(new Object(), new Object());
    }

    @Test
    void notSame_fails_whenSameReference() {
        Object obj = new Object();
        assertThrows(AssertionError.class, () -> Assert.notSame(obj, obj));
    }

    // ── StrictEqual / NotStrictEqual ─────────────────────────────────

    @Test
    void strictEqual_succeeds_whenEqual() {
        Assert.strictEqual("abc", "abc");
    }

    @Test
    void strictEqual_fails_whenNotEqual() {
        assertThrows(AssertionError.class, () -> Assert.strictEqual("a", "b"));
    }

    @Test
    void notStrictEqual_succeeds_whenNotEqual() {
        Assert.notStrictEqual("a", "b");
    }

    // ── Contains ─────────────────────────────────────────────────────

    @Test
    void contains_string_succeeds() {
        Assert.contains("world", "hello world");
    }

    @Test
    void contains_string_fails() {
        assertThrows(AssertionError.class, () -> Assert.contains("xyz", "hello"));
    }

    @Test
    void contains_string_ignoreCase() {
        Assert.contains("WORLD", "hello world", true);
    }

    @Test
    void contains_iterable_succeeds() {
        Assert.contains(2, Arrays.asList(1, 2, 3));
    }

    @Test
    void contains_iterable_fails() {
        assertThrows(AssertionError.class, () -> Assert.contains(5, Arrays.asList(1, 2, 3)));
    }

    @Test
    void contains_set_succeeds() {
        Assert.contains(1, new HashSet<>(Arrays.asList(1, 2, 3)));
    }

    @Test
    void contains_map_succeeds() {
        Map<String, Integer> map = new HashMap<>();
        map.put("key", 1);
        Assert.contains("key", map);
    }

    // ── DoesNotContain ───────────────────────────────────────────────

    @Test
    void doesNotContain_string_succeeds() {
        Assert.doesNotContain("xyz", "hello");
    }

    @Test
    void doesNotContain_string_fails() {
        assertThrows(AssertionError.class, () -> Assert.doesNotContain("world", "hello world"));
    }

    @Test
    void doesNotContain_iterable_succeeds() {
        Assert.doesNotContain(5, Arrays.asList(1, 2, 3));
    }

    // ── Empty / NotEmpty ─────────────────────────────────────────────

    @Test
    void empty_iterable_succeeds() {
        Assert.empty(Collections.emptyList());
    }

    @Test
    void empty_iterable_fails() {
        assertThrows(AssertionError.class, () -> Assert.empty(Arrays.asList(1)));
    }

    @Test
    void empty_string_succeeds() {
        Assert.empty("");
    }

    @Test
    void empty_string_fails() {
        assertThrows(AssertionError.class, () -> Assert.empty("hello"));
    }

    @Test
    void notEmpty_iterable_succeeds() {
        Assert.notEmpty(Arrays.asList(1));
    }

    @Test
    void notEmpty_iterable_fails() {
        assertThrows(AssertionError.class, () -> Assert.notEmpty(Collections.emptyList()));
    }

    // ── Single ───────────────────────────────────────────────────────

    @Test
    void single_succeeds_whenOneElement() {
        String result = Assert.single(Arrays.asList("only"));
        assertEquals("only", result);
    }

    @Test
    void single_fails_whenEmpty() {
        assertThrows(AssertionError.class, () -> Assert.single(Collections.emptyList()));
    }

    @Test
    void single_fails_whenMultiple() {
        assertThrows(AssertionError.class, () -> Assert.single(Arrays.asList(1, 2)));
    }

    @Test
    void single_withPredicate_succeeds() {
        Integer result = Assert.single(Arrays.asList(1, 2, 3), x -> x > 2);
        assertEquals(3, result);
    }

    // ── IsType / IsNotType ───────────────────────────────────────────

    @Test
    void isType_succeeds_whenExactType() {
        String result = Assert.isType(String.class, "hello");
        assertEquals("hello", result);
    }

    @Test
    void isType_fails_whenWrongType() {
        assertThrows(AssertionError.class, () -> Assert.isType(Integer.class, "hello"));
    }

    @Test
    void isType_nonExact_succeeds_whenSubtype() {
        Object obj = "hello";
        String result = Assert.isType(String.class, obj, false);
        assertEquals("hello", result);
    }

    @Test
    void isNotType_succeeds_whenDifferentType() {
        Assert.isNotType(Integer.class, "hello");
    }

    @Test
    void isNotType_fails_whenSameType() {
        assertThrows(AssertionError.class, () -> Assert.isNotType(String.class, "hello"));
    }

    // ── IsAssignableFrom / IsNotAssignableFrom ───────────────────────

    @Test
    void isAssignableFrom_succeeds() {
        Object obj = "hello";
        String result = Assert.isAssignableFrom(String.class, obj);
        assertEquals("hello", result);
    }

    @Test
    void isAssignableFrom_fails() {
        assertThrows(AssertionError.class, () -> Assert.isAssignableFrom(Integer.class, "hello"));
    }

    @Test
    void isNotAssignableFrom_succeeds() {
        Assert.isNotAssignableFrom(Integer.class, "hello");
    }

    // ── InRange / NotInRange ─────────────────────────────────────────

    @Test
    void inRange_succeeds() {
        Assert.inRange(5, 1, 10);
    }

    @Test
    void inRange_fails_whenOutOfRange() {
        assertThrows(AssertionError.class, () -> Assert.inRange(15, 1, 10));
    }

    @Test
    void notInRange_succeeds() {
        Assert.notInRange(15, 1, 10);
    }

    @Test
    void notInRange_fails_whenInRange() {
        assertThrows(AssertionError.class, () -> Assert.notInRange(5, 1, 10));
    }

    // ── Throws / ThrowsAny ───────────────────────────────────────────

    @Test
    void throws_succeeds_whenExactType() {
        IllegalArgumentException ex = Assert.throws_(IllegalArgumentException.class,
            () -> { throw new IllegalArgumentException("test"); });
        assertEquals("test", ex.getMessage());
    }

    @Test
    void throws_fails_whenNoException() {
        assertThrows(AssertionError.class,
            () -> Assert.throws_(IllegalArgumentException.class, () -> {}));
    }

    @Test
    void throws_fails_whenWrongType() {
        assertThrows(AssertionError.class,
            () -> Assert.throws_(IllegalArgumentException.class,
                () -> { throw new RuntimeException("test"); }));
    }

    @Test
    void throws_fails_whenSubtype() {
        assertThrows(AssertionError.class,
            () -> Assert.throws_(RuntimeException.class,
                () -> { throw new IllegalArgumentException("test"); }));
    }

    @Test
    void throwsAny_succeeds_whenSubtype() {
        RuntimeException ex = Assert.throwsAny(RuntimeException.class,
            () -> { throw new IllegalArgumentException("test"); });
        assertEquals("test", ex.getMessage());
    }

    // ── StartsWith / EndsWith ────────────────────────────────────────

    @Test
    void startsWith_succeeds() {
        Assert.startsWith("hello", "hello world");
    }

    @Test
    void startsWith_fails() {
        assertThrows(AssertionError.class, () -> Assert.startsWith("world", "hello world"));
    }

    @Test
    void startsWith_ignoreCase() {
        Assert.startsWith("HELLO", "hello world", true);
    }

    @Test
    void endsWith_succeeds() {
        Assert.endsWith("world", "hello world");
    }

    @Test
    void endsWith_fails() {
        assertThrows(AssertionError.class, () -> Assert.endsWith("hello", "hello world"));
    }

    // ── Matches / DoesNotMatch ───────────────────────────────────────

    @Test
    void matches_string_succeeds() {
        Assert.matches("h.*o", "hello");
    }

    @Test
    void matches_string_fails() {
        assertThrows(AssertionError.class, () -> Assert.matches("xyz", "hello"));
    }

    @Test
    void matches_pattern_succeeds() {
        Assert.matches(Pattern.compile("\\d+"), "abc123");
    }

    @Test
    void doesNotMatch_succeeds() {
        Assert.doesNotMatch("xyz", "hello");
    }

    @Test
    void doesNotMatch_fails() {
        assertThrows(AssertionError.class, () -> Assert.doesNotMatch("h.*o", "hello"));
    }

    // ── All ──────────────────────────────────────────────────────────

    @Test
    void all_succeeds_whenAllPass() {
        Assert.all(Arrays.asList(2, 4, 6), item -> Assert.true_(item % 2 == 0));
    }

    @Test
    void all_fails_whenSomeFail() {
        assertThrows(AssertionError.class,
            () -> Assert.all(Arrays.asList(1, 2, 3), item -> Assert.true_(item % 2 == 0)));
    }

    // ── Collection ───────────────────────────────────────────────────

    @Test
    void collection_succeeds_whenExactMatch() {
        Assert.collection(Arrays.asList(1, 2, 3),
            item -> Assert.equal(1, item),
            item -> Assert.equal(2, item),
            item -> Assert.equal(3, item));
    }

    @Test
    void collection_fails_whenCountMismatch() {
        assertThrows(AssertionError.class,
            () -> Assert.collection(Arrays.asList(1, 2),
                item -> {},
                item -> {},
                item -> {}));
    }

    // ── Distinct ─────────────────────────────────────────────────────

    @Test
    void distinct_succeeds_whenAllUnique() {
        Assert.distinct(Arrays.asList(1, 2, 3));
    }

    @Test
    void distinct_fails_whenDuplicates() {
        assertThrows(AssertionError.class, () -> Assert.distinct(Arrays.asList(1, 2, 2)));
    }

    // ── Subset / Superset / ProperSubset / ProperSuperset ────────────

    @Test
    void subset_succeeds() {
        Assert.subset(new HashSet<>(Arrays.asList(1, 2)),
            new HashSet<>(Arrays.asList(1, 2, 3)));
    }

    @Test
    void subset_fails() {
        assertThrows(AssertionError.class,
            () -> Assert.subset(new HashSet<>(Arrays.asList(1, 4)),
                new HashSet<>(Arrays.asList(1, 2, 3))));
    }

    @Test
    void superset_succeeds() {
        Assert.superset(new HashSet<>(Arrays.asList(1, 2, 3)),
            new HashSet<>(Arrays.asList(1, 2)));
    }

    @Test
    void properSubset_succeeds() {
        Assert.properSubset(new HashSet<>(Arrays.asList(1, 2)),
            new HashSet<>(Arrays.asList(1, 2, 3)));
    }

    @Test
    void properSubset_fails_whenEqual() {
        assertThrows(AssertionError.class,
            () -> Assert.properSubset(new HashSet<>(Arrays.asList(1, 2)),
                new HashSet<>(Arrays.asList(1, 2))));
    }

    @Test
    void properSuperset_succeeds() {
        Assert.properSuperset(new HashSet<>(Arrays.asList(1, 2, 3)),
            new HashSet<>(Arrays.asList(1, 2)));
    }

    // ── Fail ─────────────────────────────────────────────────────────

    @Test
    void fail_throwsAssertionError() {
        assertThrows(AssertionError.class, () -> Assert.fail("test failure"));
    }

    @Test
    void fail_withMessage() {
        AssertionError e = assertThrows(AssertionError.class, () -> Assert.fail("custom"));
        assertEquals("custom", e.getMessage());
    }

    // ── Multiple ─────────────────────────────────────────────────────

    @Test
    void multiple_succeeds_whenAllPass() {
        Assert.multiple(
            () -> Assert.equal(1, 1),
            () -> Assert.true_(true));
    }

    @Test
    void multiple_fails_whenSomeFail() {
        assertThrows(AssertionError.class,
            () -> Assert.multiple(
                () -> Assert.equal(1, 1),
                () -> Assert.equal(1, 2)));
    }

    // ── ReferenceEquals ──────────────────────────────────────────────

    @Test
    void referenceEquals_sameObject() {
        Object obj = new Object();
        assertTrue(Assert.referenceEquals(obj, obj));
    }

    @Test
    void referenceEquals_differentObject() {
        assertFalse(Assert.referenceEquals(new Object(), new Object()));
    }

    // ── Equals ───────────────────────────────────────────────────────

    @Test
    void equals_returnsTrue_whenEqual() {
        assertTrue(Assert.equals("a", "a"));
    }

    @Test
    void equals_returnsFalse_whenNotEqual() {
        assertFalse(Assert.equals("a", "b"));
    }

    // ── Equivalent ───────────────────────────────────────────────────

    @Test
    void equivalent_succeeds_whenEqual() {
        Assert.equivalent("a", "a", false);
    }

    @Test
    void equivalent_fails_whenNotEqual() {
        assertThrows(AssertionError.class, () -> Assert.equivalent("a", "b", false));
    }

    // ── DoesNotContain with Map ──────────────────────────────────────

    @Test
    void doesNotContain_map_succeeds() {
        Map<String, Integer> map = new HashMap<>();
        map.put("key", 1);
        Assert.doesNotContain("missing", map);
    }

    @Test
    void doesNotContain_map_fails() {
        Map<String, Integer> map = new HashMap<>();
        map.put("key", 1);
        assertThrows(AssertionError.class, () -> Assert.doesNotContain("key", map));
    }

    // ── DoesNotContain with Set ──────────────────────────────────────

    @Test
    void doesNotContain_set_succeeds() {
        Assert.doesNotContain(5, new HashSet<>(Arrays.asList(1, 2, 3)));
    }

    @Test
    void doesNotContain_set_fails() {
        assertThrows(AssertionError.class,
            () -> Assert.doesNotContain(1, new HashSet<>(Arrays.asList(1, 2, 3))));
    }

    // ── Contains with Predicate ──────────────────────────────────────

    @Test
    void contains_predicate_succeeds() {
        Assert.contains((Iterable<Integer>) Arrays.asList(1, 2, 3), x -> x > 2);
    }

    @Test
    void contains_predicate_fails() {
        assertThrows(AssertionError.class,
            () -> Assert.contains((Iterable<Integer>) Arrays.asList(1, 2, 3), x -> x > 5));
    }

    // ── DoesNotContain with Predicate ────────────────────────────────

    @Test
    void doesNotContain_predicate_succeeds() {
        Assert.doesNotContain((Iterable<Integer>) Arrays.asList(1, 2, 3), x -> x > 5);
    }

    @Test
    void doesNotContain_predicate_fails() {
        assertThrows(AssertionError.class,
            () -> Assert.doesNotContain((Iterable<Integer>) Arrays.asList(1, 2, 3), x -> x > 2));
    }

    // ── DoesNotMatch with Pattern ────────────────────────────────────

    @Test
    void doesNotMatch_pattern_succeeds() {
        Assert.doesNotMatch(Pattern.compile("xyz"), "hello");
    }

    @Test
    void doesNotMatch_pattern_fails() {
        assertThrows(AssertionError.class,
            () -> Assert.doesNotMatch(Pattern.compile("h.*o"), "hello"));
    }

    // ── Equal with custom comparer ───────────────────────────────────

    @Test
    void equal_customComparer_succeeds() {
        Assert.equal("Hello", "hello", String::equalsIgnoreCase);
    }

    @Test
    void equal_customComparer_fails() {
        assertThrows(AssertionError.class,
            () -> Assert.equal("a", "b", String::equals));
    }

    // ── NotEqual with custom comparer ────────────────────────────────

    @Test
    void notEqual_customComparer_succeeds() {
        Assert.notEqual("a", "b", String::equals);
    }

    // ── InRange with Comparator ──────────────────────────────────────

    @Test
    void inRange_comparator_succeeds() {
        Assert.inRange(5, 1, 10, Comparator.naturalOrder());
    }

    @Test
    void inRange_comparator_fails() {
        assertThrows(AssertionError.class,
            () -> Assert.inRange(15, 1, 10, Comparator.naturalOrder()));
    }

    // ── NotInRange with Comparator ───────────────────────────────────

    @Test
    void notInRange_comparator_succeeds() {
        Assert.notInRange(15, 1, 10, Comparator.naturalOrder());
    }

    // ── Throws with Supplier ─────────────────────────────────────────

    @Test
    void throws_supplier_succeeds() {
        IllegalArgumentException ex = Assert.throws_(IllegalArgumentException.class,
            () -> { throw new IllegalArgumentException("test"); });
        assertEquals("test", ex.getMessage());
    }

    // ── Equal NaN handling ───────────────────────────────────────────

    @Test
    void equal_doubleNaN_succeeds() {
        Assert.equal(Double.NaN, Double.NaN, 0.0);
    }

    @Test
    void equal_floatNaN_succeeds() {
        Assert.equal(Float.NaN, Float.NaN, 0.0f);
    }

    // ── NotEqual float/double precision ──────────────────────────────

    @Test
    void notEqual_doublePrecision_succeeds() {
        Assert.notEqual(3.14, 2.71, 1);
    }

    @Test
    void notEqual_floatPrecision_succeeds() {
        Assert.notEqual(3.14f, 2.71f, 1);
    }

    // ── IsType with exactMatch ───────────────────────────────────────

    @Test
    void isType_exactMatch_fails_forSubtype() {
        // ArrayList is a List, but not exact match
        Assert.isType(ArrayList.class, new ArrayList<>(), true);
        assertThrows(AssertionError.class,
            () -> Assert.isType(ArrayList.class, List.of(1), true));
    }

    // ── IsNotType with exactMatch ────────────────────────────────────

    @Test
    void isNotType_exactMatch_succeeds_forDifferentType() {
        Assert.isNotType(String.class, 42, true);
    }

    @Test
    void isNotType_nonExact_succeeds_forUnrelatedType() {
        Assert.isNotType(Integer.class, "hello", false);
    }
}
