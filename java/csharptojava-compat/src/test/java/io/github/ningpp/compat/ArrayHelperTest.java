package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class ArrayHelperTest {
    @Test
    void asListView_iterator_resetRestartsEnumeration() {
        CSharpGenericIList<String> view = ArrayHelper.asListView(new String[] { "a", "b", "c" });
        CSharpGenericEnumerator<String> en = view.iterator();
        assertTrue(en.moveNext());
        assertEquals("a", en.getCurrent());
        assertTrue(en.moveNext());
        assertEquals("b", en.getCurrent());
        en.reset();
        assertTrue(en.moveNext());
        assertEquals("a", en.getCurrent());
    }


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
    void asIterable_toArrayReturnsSameInstance() {
        String[] arr = new String[] { "a", "b", "c" };
        CSharpGenericIterable<String> view = ArrayHelper.asIterable(arr);

        assertSame(arr, view.toArray(new String[0]));
        assertSame(arr, view.toArray());
    }

    @Test
    void asIterable_reflectsExternalArrayChanges() {
        String[] arr = new String[] { "a", "b" };
        CSharpGenericIterable<String> view = ArrayHelper.asIterable(arr);

        arr[0] = "Z";

        assertEquals("Z", view.iterator().next());
        assertEquals(2, view.size());
    }

    @Test
    void asIterable_nullArrayReturnsNull() {
        assertNull(ArrayHelper.asIterable(null));
    }

    @Test
    void asIterable_supportsStreamOperations() {
        Integer[] arr = new Integer[] { 3, 1, 2 };
        CSharpGenericIterable<Integer> view = ArrayHelper.asIterable(arr);

        assertEquals(3, view.stream().count());
        assertEquals(1, (int) view.stream().min(Integer::compareTo).get());
    }

    @Test
    void asListView_toArrayReturnsSameInstance() {
        String[] arr = new String[] { "a", "b", "c" };
        CSharpGenericIList<String> view = ArrayHelper.asListView(arr);

        assertSame(arr, view.toArray(new String[0]));
    }

    @Test
    void asListView_sizeMatchesArrayLength() {
        Integer[] arr = new Integer[] { 1, 2, 3, 4, 5 };
        CSharpGenericIList<Integer> view = ArrayHelper.asListView(arr);

        assertEquals(5, view.size());
        assertEquals(5, view.getCount());
    }

    @Test
    void testGetLength_multiDim() {
        int[][] arr = {{1, 2, 3}, {4, 5}};
        assertEquals(5, ArrayHelper.getLength((Object) arr));
    }

    @Test
    void testClone() {
        int[] arr = {1, 2, 3};
        int[] copy = (int[]) ArrayHelper.clone(arr);
        assertArrayEquals(arr, copy);
        assertNotSame(arr, copy);
    }

    @Test
    void testGetValue_andSetValue() {
        int[][] arr = {{1, 2}, {3, 4}};
        assertEquals(3, ArrayHelper.getValue(arr, 1, 0));
        ArrayHelper.setValue(arr, 99, 1, 0);
        assertEquals(99, ArrayHelper.getValue(arr, 1, 0));
    }

    @Test
    void testResize_grow() {
        String[] arr = {"a", "b"};
        String[] result = ArrayHelper.resize(arr, 4);
        assertArrayEquals(new String[]{"a", "b", null, null}, result);
    }

    @Test
    void testResize_shrink() {
        String[] arr = {"a", "b", "c"};
        String[] result = ArrayHelper.resize(arr, 2);
        assertArrayEquals(new String[]{"a", "b"}, result);
    }

    @Test
    void testReverse() {
        Integer[] arr = {1, 2, 3, 4, 5};
        ArrayHelper.reverse(arr);
        assertArrayEquals(new Integer[]{5, 4, 3, 2, 1}, arr);
    }

    @Test
    void testFind_andFindAll() {
        Integer[] arr = {1, 2, 3, 4, 5};
        assertEquals(3, ArrayHelper.find(arr, x -> x == 3));
        Integer[] evens = ArrayHelper.findAll(arr, x -> x % 2 == 0);
        assertArrayEquals(new Integer[]{2, 4}, evens);
    }

    @Test
    void testExists_andTrueForAll() {
        Integer[] arr = {2, 4, 6};
        assertTrue(ArrayHelper.exists(arr, x -> x == 4));
        assertTrue(ArrayHelper.trueForAll(arr, x -> x % 2 == 0));
    }

    @Test
    void testConvertAll() {
        Integer[] arr = {1, 2, 3};
        String[] result = ArrayHelper.convertAll(arr, Object::toString);
        assertArrayEquals(new String[]{"1", "2", "3"}, result);
    }
}
