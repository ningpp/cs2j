package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;

/**
 * A concrete collection class names its element type in the generic supertype it
 * implements ({@code class StringList implements CSharpGenericIList<String>}), so the
 * type-argument query has to read the class hierarchy. Before the hierarchy walk,
 * {@code getGenericArguments} fell through to the type-parameter bounds, returned an
 * empty array for such classes, and the collection converter built its element
 * converter for Object: every element came back as a JsonElement and the generated
 * {@code add(String)} threw ClassCastException.
 */
class TypeHelperDeclaredSupertypeArgumentsTest {

    static class StringList implements CSharpGenericIList<String> {
        private final List<String> values = new ArrayList<>();

        @Override public String get(int index) { return values.get(index); }
        @Override public String set(int index, String value) { return values.set(index, value); }
        @Override public int getCount() { return values.size(); }
        @Override public boolean getIsReadOnly() { return false; }
        @Override public boolean add(String item) { return values.add(item); }
        @Override public void clear() { values.clear(); }
        @Override public boolean contains(Object item) { return values.contains(item); }
        @Override public void copyTo(String[] array, int arrayIndex) {
            for (int i = 0; i < values.size(); i++) {
                array[arrayIndex + i] = values.get(i);
            }
        }
        @Override public CSharpGenericEnumerator<String> iterator() {
            return CSharpGenericEnumerator.from(values.iterator());
        }
        @Override public int indexOf(Object item) { return values.indexOf(item); }
        @Override public void insert(int index, String item) { values.add(index, item); }
        @Override public boolean remove(Object item) { return values.remove(item); }
        @Override public void removeAt(int index) { values.remove(index); }
    }

    /** Element type named by the implemented interface, not by a type parameter. */
    @Test
    void getGenericArguments_readsElementTypeFromImplementedInterface() {
        assertArrayEquals(new Class<?>[] { String.class },
            TypeHelper.getGenericArguments(StringList.class));
    }

    /** Derived class inherits the argument its base class declared. */
    static final class DerivedStringList extends StringList {
    }

    @Test
    void getGenericArguments_readsElementTypeFromSuperclass() {
        assertArrayEquals(new Class<?>[] { String.class },
            TypeHelper.getGenericArguments(DerivedStringList.class));
    }

    /** Two arguments keep their declared order for a generic collection supertype. */
    static final class StringIntMap extends java.util.LinkedHashMap<String, Integer> {
    }

    @Test
    void getGenericArguments_readsBothArgumentsInDeclaredOrder() {
        assertArrayEquals(new Class<?>[] { String.class, Integer.class },
            TypeHelper.getGenericArguments(StringIntMap.class));
    }

    /**
     * A non-collection class must not report arguments from an unrelated generic
     * interface: String implements Comparable&lt;String&gt;, and that is not an element type.
     * (Boxed primitives keep their pre-existing Nullable&lt;T&gt; shortcut.)
     */
    @Test
    void getGenericArguments_ofANonCollectionClassStaysEmpty() {
        assertArrayEquals(new Class<?>[0], TypeHelper.getGenericArguments(String.class));
        assertArrayEquals(new Class<?>[0], TypeHelper.getGenericArguments(Object.class));
        assertArrayEquals(new Class<?>[0], TypeHelper.getGenericArguments(java.time.LocalDate.class));
    }
}
