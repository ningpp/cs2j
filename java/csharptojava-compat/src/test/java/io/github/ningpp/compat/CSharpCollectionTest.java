package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertSame;

import java.util.Collections;
import org.junit.jupiter.api.Test;

class CSharpCollectionTest {
    @Test
    void derivedCollectionCanImplementCSharpCollection() {
        DerivedCollection collection = new DerivedCollection();

        assertEquals(0, collection.size());
        assertSame(collection, collection.getSyncRoot());
    }

    @Test
    void csharpCollectionIsIterable() {
        DerivedCollection collection = new DerivedCollection();
        Iterable<Object> iterable = collection;
        assertNotNull(iterable);
    }

    static class DerivedCollection implements CSharpCollection {
        @Override
        public int size() {
            return 0;
        }

        @Override
        public CSharpEnumerator iterator() {
            return CSharpEnumerator.from(Collections.emptyList().iterator());
        }

        @Override
        public void copyTo(CSharpArray array, int index) {
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
}
