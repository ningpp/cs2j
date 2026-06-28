package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

import java.util.Collections;
import java.util.Iterator;
import org.junit.jupiter.api.Test;

class CSharpCollectionTest {
    @Test
    void derivedCollectionCanExtendRawIterableBase() {
        DerivedCollection collection = new DerivedCollection();

        assertEquals(0, collection.size());
        assertSame(collection, collection.getSyncRoot());
    }

    static class RawIterableBase implements Iterable {
        @Override
        public Iterator iterator() {
            return Collections.emptyIterator();
        }
    }

    static class DerivedCollection extends RawIterableBase implements CSharpCollection {
        @Override
        public int size() {
            return 0;
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
