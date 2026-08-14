package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.List;

class ImmutableCollectionsTest {

    // ---- ImmutableHashSet: construction ----

    @Test
    void create_emptyAndVarargs() {
        assertTrue(ImmutableHashSet.<String>create().isEmpty());
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b", "c");
        assertEquals(3, set.getCount());
        assertTrue(set.contains("b"));
    }

    @Test
    void createRange_copiesElements() {
        List<String> list = Arrays.asList("a", "b", "a");
        ImmutableHashSet<String> set = ImmutableHashSet.createRange(list);
        assertEquals(2, set.getCount());
    }

    @Test
    void empty_singleton_isEmpty() {
        assertTrue(ImmutableHashSet.Empty.isEmpty());
    }

    @Test
    void defaultKeyComparer_isDefault() {
        ImmutableHashSet<String> set = ImmutableHashSet.create();
        assertSame(EqualityComparer.getDefault(), set.getKeyComparer());
    }

    // ---- ImmutableHashSet: operations ----

    @Test
    void add_returnsNewSetWithItem() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a");
        ImmutableHashSet<String> added = set.add("b");
        assertEquals(2, added.getCount());
        assertEquals(1, set.getCount());
        assertTrue(added.contains("b"));
    }

    @Test
    void clear_returnsEmptySet() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b");
        assertTrue(set.clear().isEmpty());
    }

    @Test
    void removeCSharp_returnsSetWithoutItem() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b");
        ImmutableHashSet<String> removed = set.removeCSharp("a");
        assertEquals(1, removed.getCount());
        assertFalse(removed.contains("a"));
        assertEquals(2, set.getCount());
    }

    @Test
    void union_mergesItems() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b");
        ImmutableHashSet<String> union = set.union(Arrays.asList("b", "c"));
        assertEquals(3, union.getCount());
        assertTrue(union.contains("c"));
    }

    @Test
    void intersect_keepsCommonItems() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b", "c");
        ImmutableHashSet<String> intersect = set.intersect(Arrays.asList("b", "c", "d"));
        assertEquals(2, intersect.getCount());
        assertTrue(intersect.contains("b"));
        assertFalse(intersect.contains("a"));
    }

    @Test
    void except_removesOtherItems() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b", "c");
        ImmutableHashSet<String> except = set.except(Arrays.asList("b"));
        assertEquals(2, except.getCount());
        assertFalse(except.contains("b"));
    }

    @Test
    void symmetricExcept_keepsOnlyInOne() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b");
        ImmutableHashSet<String> result = set.symmetricExcept(Arrays.asList("b", "c"));
        assertEquals(2, result.getCount());
        assertTrue(result.contains("a"));
        assertTrue(result.contains("c"));
        assertFalse(result.contains("b"));
    }

    // ---- ImmutableHashSet: predicates ----

    @Test
    void setPredicates_matchDotNetSemantics() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b", "c");
        ImmutableHashSet<String> other = ImmutableHashSet.create("b", "c", "d");

        assertTrue(set.isSubsetOf(CSharpGenericIterable.from(ImmutableHashSet.create("a", "b", "c", "d"))));
        assertFalse(set.isSubsetOf(CSharpGenericIterable.from(other)));

        assertTrue(set.isSupersetOf(CSharpGenericIterable.from(ImmutableHashSet.create("a", "b"))));
        assertFalse(set.isSupersetOf(CSharpGenericIterable.from(other)));

        assertTrue(set.isProperSubsetOf(CSharpGenericIterable.from(ImmutableHashSet.create("a", "b", "c", "d"))));
        assertFalse(set.isProperSubsetOf(CSharpGenericIterable.from(ImmutableHashSet.create("a", "b", "c"))));

        assertTrue(set.isProperSupersetOf(CSharpGenericIterable.from(ImmutableHashSet.create("a", "b"))));
        assertFalse(set.isProperSupersetOf(CSharpGenericIterable.from(ImmutableHashSet.create("a", "b", "c"))));

        assertTrue(set.overlaps(CSharpGenericIterable.from(other)));
        assertFalse(set.overlaps(CSharpGenericIterable.from(ImmutableHashSet.create("x", "y"))));

        assertTrue(set.setEquals(CSharpGenericIterable.from(ImmutableHashSet.create("a", "b", "c"))));
        assertFalse(set.setEquals(CSharpGenericIterable.from(other)));
    }

    @Test
    void tryGetValue_findsEqualElement() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b");
        ObjectHolder<String> holder = new ObjectHolder<>();
        assertTrue(set.tryGetValue("b", holder));
        assertEquals("b", holder.value);
        assertFalse(set.tryGetValue("x", holder));
    }

    @Test
    void withComparer_usesNewComparer() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b");
        IEqualityComparer<String> upper = new IEqualityComparer<String>() {
            @Override
            public boolean equals(String x, String y) {
                return x.equalsIgnoreCase(y);
            }

            @Override
            public int hashCode(String obj) {
                return obj.toUpperCase().hashCode();
            }
        };
        ImmutableHashSet<String> upperSet = set.withComparer(upper);
        assertSame(upper, upperSet.getKeyComparer());
        assertTrue(upperSet.contains("A"));
        assertEquals(2, upperSet.getCount());
    }

    // ---- ImmutableHashSet.Builder ----

    @Test
    void builder_mutatesAndToImmutable() {
        ImmutableHashSet.Builder<String> builder = ImmutableHashSet.createBuilder();
        assertTrue(builder.add("a"));
        assertTrue(builder.add("b"));
        assertFalse(builder.add("a"));
        assertTrue(builder.remove("a"));
        builder.unionWith(CSharpGenericIterable.from(Arrays.asList("c", "d")));
        builder.exceptWith(CSharpGenericIterable.from(Arrays.asList("d")));
        assertEquals(2, builder.getCount());
        ImmutableHashSet<String> set = builder.toImmutable();
        assertTrue(set.contains("b"));
        assertTrue(set.contains("c"));
        assertFalse(set.contains("a"));
        assertFalse(set.contains("d"));
    }

    @Test
    void builder_setOperations() {
        ImmutableHashSet.Builder<String> builder = ImmutableHashSet.createBuilder();
        builder.add("a");
        builder.add("b");
        builder.intersectWith(CSharpGenericIterable.from(Arrays.asList("b", "c")));
        assertEquals(1, builder.getCount());
        builder.symmetricExceptWith(CSharpGenericIterable.from(Arrays.asList("b", "d")));
        assertEquals(1, builder.getCount());
        assertTrue(builder.contains("d"));
        assertFalse(builder.contains("b"));
    }

    @Test
    void builder_setKeyComparer_rebuilds() {
        ImmutableHashSet.Builder<String> builder = ImmutableHashSet.createBuilder();
        builder.add("a");
        builder.add("b");
        IEqualityComparer<String> upper = new IEqualityComparer<String>() {
            @Override
            public boolean equals(String x, String y) {
                return x.equalsIgnoreCase(y);
            }

            @Override
            public int hashCode(String obj) {
                return obj.toUpperCase().hashCode();
            }
        };
        builder.setKeyComparer(upper);
        assertSame(upper, builder.getKeyComparer());
        builder.add("B");
        assertEquals(2, builder.getCount());
        ImmutableHashSet<String> set = builder.toImmutable();
        assertTrue(set.contains("A"));
    }

    @Test
    void toBuilder_seedsWithItems() {
        ImmutableHashSet<String> set = ImmutableHashSet.create("a", "b");
        ImmutableHashSet.Builder<String> builder = set.toBuilder();
        assertEquals(2, builder.getCount());
        builder.add("c");
        assertEquals(3, builder.getCount());
        assertEquals(2, set.getCount());
    }

    // ---- ImmutableStack ----

    @Test
    void stack_create_push_pop() {
        ImmutableStack<String> stack = ImmutableStack.create();
        assertTrue(stack.isEmpty());
        ImmutableStack<String> pushed = stack.push("a").push("b");
        assertEquals(2, pushed.getCount());
        assertEquals("b", pushed.peek());
        ImmutableStack<String> popped = pushed.pop();
        assertEquals("a", popped.peek());
        assertEquals(1, popped.getCount());
        assertTrue(popped.pop().isEmpty());
    }

    @Test
    void stack_createRange_pushesInOrder() {
        ImmutableStack<String> stack = ImmutableStack.createRange(Arrays.asList("a", "b", "c"));
        assertEquals("c", stack.peek());
        assertEquals("b", stack.pop().peek());
    }

    @Test
    void stack_popOut_returnsPoppedValue() {
        ImmutableStack<String> stack = ImmutableStack.create("a", "b");
        ObjectHolder<String> holder = new ObjectHolder<>();
        ImmutableStack<String> popped = stack.pop(holder);
        assertEquals("b", holder.value);
        assertEquals(1, popped.getCount());
        assertEquals("a", popped.peek());
    }

    @Test
    void stack_clear_returnsEmpty() {
        ImmutableStack<String> stack = ImmutableStack.create("a", "b");
        assertTrue(stack.clear().isEmpty());
    }

    @Test
    void stack_empty_singleton() {
        assertTrue(ImmutableStack.Empty.isEmpty());
    }

    @Test
    void stack_iteration_isTopFirst() {
        ImmutableStack<String> stack = ImmutableStack.create("a", "b");
        List<String> seen = new java.util.ArrayList<>();
        for (String item : stack) {
            seen.add(item);
        }
        assertEquals(Arrays.asList("b", "a"), seen);
    }

    // ---- IImmutableStack interface ----

    @Test
    void stack_interfaceView_exposesOperations() {
        IImmutableStack<String> stack = ImmutableStack.create("a");
        IImmutableStack<String> pushed = stack.push("b");
        assertEquals(2, pushed.size());
        assertEquals("b", pushed.peek());
        IImmutableStack<String> popped = pushed.pop();
        assertEquals("a", popped.peek());
        assertTrue(popped.pop().isEmpty());
        assertTrue(pushed.clear().isEmpty());
    }
}
