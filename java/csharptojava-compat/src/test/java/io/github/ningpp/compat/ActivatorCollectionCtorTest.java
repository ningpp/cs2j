package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import org.junit.jupiter.api.Test;

/**
 * Reflection-based instantiation must tolerate the parallel compat collection
 * hierarchies: a generated C# collection such as CaptureCollection implements
 * CSharpIList&lt;T&gt; / CSharpCollection but not CSharpICollection&lt;T&gt;, so a
 * constructor whose parameter is CSharpICollection&lt;T&gt; used to fail with
 * "No matching constructor found" even though C# accepts the assignment.
 * {@link Activator} resolves it through the compat adapter
 * ({@link CSharpICollection#fromTyped(Object)}) only after a direct match fails.
 */
class ActivatorCollectionCtorTest {

    /** Stands in for a generated C# collection that only implements CSharpIList&lt;T&gt;. */
    static final class StringList implements CSharpIList<String> {
        private final List<String> items;

        StringList(String... items) {
            this.items = Arrays.asList(items);
        }

        @Override
        public int getCount() {
            return items.size();
        }

        @Override
        public CSharpGenericEnumerator iterator() {
            return CSharpGenericEnumerator.from(items.iterator());
        }

        @Override
        public boolean getIsSynchronized() {
            return false;
        }

        @Override
        public Object getSyncRoot() {
            return this;
        }
    }

    static final class ICollectionConsumer {
        final CSharpICollection<String> collection;

        ICollectionConsumer(CSharpICollection<String> collection) {
            this.collection = collection;
        }
    }

    /**
     * The converter's constructor shape for a generic class that needs a runtime
     * type token: the restored C# constructor delegates to an injected-trailing-Class
     * sibling. Activator prefers the sibling so `new T[]` materializes a typed array.
     */
    static final class TokenAwareConsumer {
        final CSharpICollection<String> collection;
        final Class<?> elementType;

        TokenAwareConsumer(CSharpICollection<String> collection) {
            this(collection, Object.class);
        }

        TokenAwareConsumer(CSharpICollection<String> collection, Class<?> elementType) {
            this.collection = collection;
            this.elementType = elementType;
        }

        @SuppressWarnings({ "rawtypes", "unchecked" })
        Object[] items() {
            Object[] items = (Object[]) CSharpArray.createInstance(elementType, collection.getCount()).unwrap();
            // Same erased call the generated debugger proxy makes: T[] erases to Object[].
            ((CSharpICollection) collection).copyTo(items, 0);
            return items;
        }
    }

    @Test
    void adaptsCSharpIListArgumentToICollectionParameter() {
        Object instance = Activator.createInstance(
            ICollectionConsumer.class, new Object[] { new StringList("a", "b") });

        ICollectionConsumer consumer = (ICollectionConsumer) instance;
        assertNotNull(consumer.collection);
        assertEquals(2, consumer.collection.getCount());
        assertTrue(consumer.collection.contains("b"));
        List<String> seen = new ArrayList<>();
        for (String item : consumer.collection) {
            seen.add(item);
        }
        assertEquals(Arrays.asList("a", "b"), seen);
    }

    @Test
    void adaptsJavaCollectionArgumentToICollectionParameter() {
        ICollectionConsumer consumer = (ICollectionConsumer) Activator.createInstance(
            ICollectionConsumer.class, new Object[] { List.of("x", "y") });

        assertNotNull(consumer.collection);
        assertEquals(2, consumer.collection.getCount());
        assertTrue(consumer.collection.contains("x"));
        List<String> seen = new ArrayList<>();
        for (String item : consumer.collection) {
            seen.add(item);
        }
        assertEquals(List.of("x", "y"), seen);
    }

    @Test
    void keepsDirectlyCompatibleArgumentUnwrapped() {
        CSharpList<String> original = new CSharpList<>();
        original.add("a");

        ICollectionConsumer consumer = (ICollectionConsumer) Activator.createInstance(
            ICollectionConsumer.class, new Object[] { original });

        assertSame(original, consumer.collection);
    }

    @Test
    void prefersTokenAugmentedSiblingAndInfersElementType() {
        TokenAwareConsumer consumer = (TokenAwareConsumer) Activator.createInstance(
            TokenAwareConsumer.class, new Object[] { new StringList("a", "b") });

        assertEquals(String.class, consumer.elementType);
        assertEquals(2, consumer.collection.getCount());
        Object[] items = consumer.items();
        assertEquals(String[].class, items.getClass());
        assertEquals(Arrays.asList("a", "b"), Arrays.asList(items));
    }
}
