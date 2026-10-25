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
 *
 * <p>The C# non-generic <c>System.Collections.ICollection</c> maps here too, and
 * it only requires Count/CopyTo/IsSynchronized/SyncRoot. Generated views backed
 * by it therefore implement just the collection surface, so every pure IList
 * member carries a default: mutators throw UnsupportedOperationException (C#
 * throws NotSupportedException for a read-only view), and IsFixedSize/IsReadOnly
 * report false. removeAt returns Object rather than void so a generated C#
 * RemoveAt that yields the removed item (e.g. XmlAttributeCollection.RemoveAt
 * returning XmlAttribute) overrides it covariantly instead of clashing with an
 * incompatible return type.</p>
 */
public interface CSharpIList<T> extends CSharpCollection {
    default int add(Object value) { throw new UnsupportedOperationException(); }
    default void clear() { throw new UnsupportedOperationException(); }
    default boolean contains(Object value) { throw new UnsupportedOperationException(); }
    default int indexOf(Object value) { throw new UnsupportedOperationException(); }
    default void insert(int index, Object value) { throw new UnsupportedOperationException(); }
    default boolean getIsFixedSize() { return false; }
    default boolean getIsReadOnly() { return false; }
    default void remove(Object value) { throw new UnsupportedOperationException(); }
    default Object removeAt(int index) { throw new UnsupportedOperationException(); }
    default T get(int index) { throw new UnsupportedOperationException(); }
    default Object set(int index, Object value) { throw new UnsupportedOperationException(); }

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
            public Object removeAt(int index) {
                list.removeAt(index);
                return null;
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
