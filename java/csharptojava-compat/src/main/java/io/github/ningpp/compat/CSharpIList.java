package io.github.ningpp.compat;

/**
 * C# non-generic IList compat view.
 *
 * <p>The C# generic <c>IList&lt;T&gt;</c>/<c>ICollection&lt;T&gt;</c> interfaces are
 * also mapped here. The type parameter is intentionally unused: all members keep
 * Object parameters, so a generated read-only collection that also declares the
 * C# generic (T-parameterized) explicit implementations does not hit Java's
 * "same erasure, neither overrides" clash between the generic and non-generic
 * flavours. Element accessors stay covariant (a generated <c>Match get(int)</c>
 * overrides <c>Object get(int)</c>).</p>
 */
public interface CSharpIList<T> extends CSharpCollection {
    int add(Object value);
    void clear();
    boolean contains(Object value);
    int indexOf(Object value);
    void insert(int index, Object value);
    boolean getIsFixedSize();
    boolean getIsReadOnly();
    void remove(Object value);
    void removeAt(int index);
    default T get(int index) { throw new UnsupportedOperationException(); }
    Object set(int index, Object value);

    /**
     * Adapts a generic list (CSharpList / CSharpGenericIList) to the non-generic
     * C# IList surface. C# allows passing a List&lt;T&gt; where IList is expected;
     * the Java interfaces are unrelated, so the converter wraps the argument or
     * cast with this bridge. add(Object) returns the insertion position like C#
     * IList.Add.
     */
    @SuppressWarnings({ "unchecked", "rawtypes" })
    static CSharpIList from(CSharpGenericIList<?> list) {
        if (list == null) {
            return null;
        }
        return new CSharpIList() {
            @Override
            public int add(Object value) {
                int count = ((CSharpGenericIList) list).getCount();
                if (((CSharpGenericIList) list).add(value)) {
                    return count;
                }
                return -1;
            }
            @Override
            public void clear() {
                list.clear();
            }
            @Override
            public boolean contains(Object value) {
                return list.contains(value);
            }
            @Override
            public int indexOf(Object value) {
                return list.indexOf(value);
            }
            @Override
            public void insert(int index, Object value) {
                ((CSharpGenericIList) list).insert(index, value);
            }
            @Override
            public boolean getIsFixedSize() {
                return false;
            }
            @Override
            public boolean getIsReadOnly() {
                return list.getIsReadOnly();
            }
            @Override
            public void remove(Object value) {
                ((CSharpGenericIList) list).remove(value);
            }
            @Override
            public void removeAt(int index) {
                list.removeAt(index);
            }
            @Override
            public Object get(int index) {
                return list.get(index);
            }
            @Override
            public Object set(int index, Object value) {
                return ((CSharpGenericIList) list).set(index, value);
            }
            public void copyTo(CSharpArray array, int index) {
                int i = 0;
                for (Object element : list) {
                    array.setValue(element, index + i);
                    i++;
                }
            }
            @Override
            public boolean getIsSynchronized() {
                return false;
            }
            @Override
            public Object getSyncRoot() {
                return list;
            }
            @Override
            public int getCount() {
                return list.getCount();
            }
            @Override
            public CSharpGenericEnumerator<Object> iterator() {
                return CSharpGenericEnumerator.from((java.util.Iterator) list.iterator());
            }
        };
    }
}
