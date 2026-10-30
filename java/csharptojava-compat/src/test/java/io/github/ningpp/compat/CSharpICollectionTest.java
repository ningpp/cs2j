package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Arrays;
import java.util.List;
import org.junit.jupiter.api.Test;

class CSharpICollectionTest {
    @Test
    void from_javaUtilCollection_adaptsToCSharpICollection() {
        List<String> list = Arrays.asList("a", "b");
        CSharpICollection<Object> adapted = CSharpICollection.from(list);
        assertEquals(2, adapted.getCount());
        assertTrue(adapted.contains("a"));
        assertFalse(adapted.isEmpty());
    }

    @Test
    void fromTyped_csharpHashSet_containsTrueAndFalse() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        set.add("alpha");
        set.add("beta");
        CSharpICollection<String> collection = CSharpICollection.fromTyped(set);
        assertTrue(collection.contains("alpha"));
        assertTrue(collection.contains("beta"));
        assertFalse(collection.contains("gamma"));
        assertFalse(collection.contains(null));
    }

    @Test
    void fromTyped_csharpList_containsTrueAndFalse() {
        CSharpList<String> list = new CSharpList<>();
        list.add("alpha");
        list.add(null);
        CSharpICollection<String> collection = CSharpICollection.fromTyped(list);
        assertEquals(2, collection.getCount());
        assertTrue(collection.contains("alpha"));
        assertFalse(collection.contains("gamma"));
        assertTrue(collection.contains(null));
    }

    @Test
    void from_csharpCollectionAdapter_containsScansElements() {
        CSharpCollection shaped = csharpCollectionOf(Arrays.<Object>asList("x", null));

        CSharpICollection<Object> adapted = CSharpICollection.from(shaped);
        assertEquals(2, adapted.getCount());
        assertTrue(adapted.contains("x"));
        assertFalse(adapted.contains("y"));
        assertTrue(adapted.contains(null));

        CSharpICollection<Object> typed = CSharpICollection.fromTyped(shaped);
        assertTrue(typed.contains("x"));
        assertFalse(typed.contains("y"));

        assertThrows(UnsupportedOperationException.class, () -> adapted.add("z"));
        assertThrows(UnsupportedOperationException.class, () -> adapted.remove("x"));
        assertThrows(UnsupportedOperationException.class, adapted::clear);
    }

    @Test
    void emptyCollection_containsReturnsFalse() {
        CSharpICollection<String> emptySet = CSharpICollection.fromTyped(new CSharpHashSet<String>());
        assertFalse(emptySet.contains("alpha"));
        assertFalse(emptySet.contains(null));

        CSharpICollection<Object> emptyAdapter = CSharpICollection.from(csharpCollectionOf(Arrays.<Object>asList()));
        assertFalse(emptyAdapter.contains("x"));
        assertFalse(emptyAdapter.contains(null));
    }

    private static CSharpCollection csharpCollectionOf(List<Object> values) {
        return new CSharpCollection() {
            @Override
            public CSharpGenericEnumerator iterator() {
                return CSharpGenericEnumerator.from(values.iterator());
            }

            @Override
            public int getCount() {
                return values.size();
            }

            @Override
            public boolean getIsSynchronized() {
                return false;
            }

            @Override
            public Object getSyncRoot() {
                return this;
            }
        };
    }
}
