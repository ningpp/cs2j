package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class ArrayHelperTest {
    @Test
    void copyArray_referenceArray_copiesContainerAndPreservesElementsAndNulls() {
        String[] source = new String[] { "a", null, "c" };

        String[] copy = ArrayHelper.copyArray(source);

        assertArrayEquals(source, copy);
        assertNotSame(source, copy);
    }

    @Test
    void copyArray_referenceArrayFactory_copiesObjectArrayToTypedArray() {
        Object[] source = new Object[] { "a", null, "c" };

        String[] copy = ArrayHelper.copyArray(source, String[]::new);

        assertArrayEquals(new String[] { "a", null, "c" }, copy);
        assertEquals(String[].class, copy.getClass());
    }

    @Test
    void copyArray_intArray_copiesPrimitiveArray() {
        int[] source = new int[] { 1, 2, 3 };

        int[] copy = ArrayHelper.copyArray(source);

        assertArrayEquals(source, copy);
        assertNotSame(source, copy);
    }

    @Test
    void toIntArray_unboxesIntegerArrayAndPreservesValues() {
        Integer[] source = new Integer[] { 1, 2, 3 };

        int[] copy = ArrayHelper.toIntArray(source);

        assertArrayEquals(new int[] { 1, 2, 3 }, copy);
    }

    @Test
    void copyStructArray_clonesNonNullElementsAndPreservesNulls() {
        MutablePoint first = new MutablePoint(10);
        MutablePoint[] source = new MutablePoint[] { first, null };

        MutablePoint[] copy = ArrayHelper.copyStructArray(source, MutablePoint[]::new, MutablePoint::clone);

        assertNotSame(source, copy);
        assertNotSame(first, copy[0]);
        assertEquals(10, copy[0].x);
        assertNull(copy[1]);

        first.x = 20;
        assertEquals(10, copy[0].x);
    }

    private static final class MutablePoint implements Cloneable {
        int x;

        MutablePoint(int x) {
            this.x = x;
        }

        @Override
        public MutablePoint clone() {
            try {
                return (MutablePoint) super.clone();
            } catch (CloneNotSupportedException ex) {
                throw new RuntimeException(ex);
            }
        }
    }

    @Test
    void asListView_setWritesThroughToArray() {
        String[] arr = new String[] { "a", "b", "c" };
        CSharpGenericIList<String> view = ArrayHelper.asListView(arr);

        view.set(1, "X");

        assertEquals("X", view.get(1));
        assertEquals("X", arr[1]); // write-through to backing array
    }

    @Test
    void asListView_reflectsExternalArrayChanges() {
        String[] arr = new String[] { "a", "b", "c" };
        CSharpGenericIList<String> view = ArrayHelper.asListView(arr);

        arr[0] = "Z";

        assertEquals("Z", view.get(0));
    }

    @Test
    void asListView_nullArrayReturnsNull() {
        assertNull(ArrayHelper.asListView(null));
    }

    @Test
    void asListView_sizeMatchesArrayLength() {
        Integer[] arr = new Integer[] { 1, 2, 3, 4, 5 };
        CSharpGenericIList<Integer> view = ArrayHelper.asListView(arr);

        assertEquals(5, view.size());
        assertEquals(5, view.getCount());
    }
}
