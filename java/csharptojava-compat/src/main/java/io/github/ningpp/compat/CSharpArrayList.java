package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.Comparator;
import java.util.Iterator;
import java.util.List;
import java.util.Objects;

/**
 * C# {@code System.Collections.ArrayList} — a NON-generic list.
 *
 * <p>{@link #asGenericList()} exposes it as {@link CSharpGenericIList}&lt;Object&gt;, the shape a
 * converted {@code IList<T>} consumer expects. The class cannot implement that interface directly:
 * C# {@code IList.Add} returns {@code int} while {@code ICollection<T>.Add} returns {@code boolean},
 * a conflict Java resolves by requiring one signature, and the converted class needs the
 * {@code int} form.</p>
 */
public class CSharpArrayList implements CSharpIList, Cloneable {
    private final ArrayList<Object> list;
    private final Object syncRoot = new Object();

    public CSharpArrayList() { this.list = new ArrayList<>(); }
    public CSharpArrayList(int capacity) { this.list = new ArrayList<>(capacity); }
    public CSharpArrayList(Collection<?> c) { this.list = new ArrayList<>(c); }
    /** C# {@code new ArrayList(object[])}: a C# array is IEnumerable; Java arrays are not Collection. */
    public CSharpArrayList(Object[] values) {
        this.list = new ArrayList<>(Math.max(values.length, 0));
        Collections.addAll(this.list, values);
    }
    public CSharpArrayList(CSharpCollection c) {
        this.list = new ArrayList<>();
        var en = c.iterator();
        while (en.moveNext()) {
            this.list.add(en.getCurrent());
        }
    }

    @Override public int add(Object value) { list.add(value); return list.size() - 1; }
    @Override public void clear() { list.clear(); }
    @Override public boolean contains(Object value) { return list.contains(value); }
    @Override public int indexOf(Object value) { return list.indexOf(value); }
    @Override public void insert(int index, Object value) { list.add(index, value); }
    @Override public boolean getIsFixedSize() { return false; }
    @Override public boolean getIsReadOnly() { return false; }
    @Override public boolean remove(Object value) { return list.remove(value); }
    @Override public Object removeAt(int index) { return list.remove(index); }
    @Override public Object get(int index) { return list.get(index); }
    @Override public Object set(int index, Object value) { return list.set(index, value); }
    public int size() { return list.size(); }
    @Override public int getCount() { return list.size(); }
    public void copyTo(CSharpArray array, int index) {
        for (int i = 0; i < list.size(); i++) array.setValue(list.get(i), index + i);
    }
    @Override public boolean getIsSynchronized() { return false; }
    @Override public Object getSyncRoot() { return syncRoot; }
    @Override public CSharpGenericEnumerator<Object> iterator() {
        return CSharpGenericEnumerator.<Object>from(list.iterator());
    }

    /**
     * A LIVE view of this list as {@link CSharpGenericIList}&lt;Object&gt;: every mutation reaches
     * the same backing list, so a converted consumer (for example the generated
     * {@code IListOfTConverter}) can populate a {@code CSharpArrayList} through the generic
     * interface and the caller still sees a {@code CSharpArrayList} instance afterwards.
     *
     * <p>The view is needed because the class cannot implement the generic interface itself:
     * C# {@code IList.Add} returns {@code int} and {@code ICollection<T>.Add} returns
     * {@code boolean}, and Java requires a single signature for both.</p>
     */
    public CSharpGenericIList<Object> asGenericList() {
        return new CSharpGenericIList<Object>() {
            @Override public CSharpGenericEnumerator<Object> iterator() {
                return CSharpArrayList.this.iterator();
            }
            @Override public int getCount() { return list.size(); }
            @Override public boolean getIsReadOnly() { return false; }
            @Override public boolean getIsFixedSize() { return false; }
            @Override public void copyTo(Object[] array, int arrayIndex) {
                for (int i = 0; i < list.size(); i++) {
                    array[arrayIndex + i] = list.get(i);
                }
            }
            @Override public boolean add(Object item) { list.add(item); return true; }
            @Override public void clear() { list.clear(); }
            @Override public boolean contains(Object item) { return list.contains(item); }
            @Override public int indexOf(Object item) { return list.indexOf(item); }
            @Override public void insert(int index, Object item) { list.add(index, item); }
            @Override public boolean remove(Object item) { return list.remove(item); }
            @Override public void removeAt(int index) { list.remove(index); }
            @Override public Object get(int index) { return list.get(index); }
            @Override public Object set(int index, Object value) { return list.set(index, value); }
        };
    }

    public int getCapacity() { return -1; } // ArrayList doesn't expose capacity
    public void setCapacity(int value) { list.ensureCapacity(value); }
    public void addRange(Collection<?> c) { list.addAll(c); }
    public void addRange(Object[] arr) { for (Object item : arr) list.add(item); }
    // CSharpArrayList itself is NOT a java.util.Collection (it goes through the
    // compat CSharpIList view), so addRange(Collection) never accepts another
    // CSharpArrayList; generated ArrayList.AddRange(other) call sites need this.
    public void addRange(CSharpArrayList other) { list.addAll(other.list); }
    public void insertRange(int index, Collection<?> c) { list.addAll(index, c); }
    // NOTE: no insertRange(int, Iterable) overload on purpose. A CSharpIList
    // argument is applicable to both insertRange(int, Iterable) and
    // insertRange(int, CSharpIList) and javac cannot pick the more specific one
    // (raw CSharpIList vs Iterable<?>) — it reports the call as ambiguous.
    public void insertRange(int index, CSharpIList c) {
        for (int i = 0; i < c.getCount(); i++) {
            list.add(index + i, c.get(i));
        }
    }
    public void insertRange(int index, Object[] arr) {
        for (int i = 0; i < arr.length; i++) {
            list.add(index + i, arr[i]);
        }
    }
    public int binarySearch(Object value) {
        return Collections.binarySearch(list, value, (a, b) -> CSharpDefaultComparer.getDefault().compare(a, b));
    }
    public int binarySearch(Object value, Comparator<?> comparer) { return Collections.binarySearch(list, value, (Comparator<Object>) comparer); }
    public CSharpArrayList getRange(int index, int count) {
        CSharpArrayList result = new CSharpArrayList(list.subList(index, index + count));
        return result;
    }
    public void removeRange(int index, int count) {
        list.subList(index, index + count).clear();
    }
    public void reverse() { Collections.reverse(list); }
    public void reverse(int index, int count) {
        for (int i = 0; i < count / 2; i++) {
            int a = index + i, b = index + count - 1 - i;
            Object tmp = list.get(a); list.set(a, list.get(b)); list.set(b, tmp);
        }
    }
    public void sort() { list.sort(null); }
    public void sort(Comparator<?> comparer) {
        Comparator<Object> c = (Comparator<Object>) comparer;
        if (c == null) {
            list.sort(null);
            return;
        }
        try {
            list.sort(c);
        } catch (IllegalArgumentException e) {
            // TimSort transitivity violation - fall back to insertion sort
            int n = list.size();
            for (int i = 1; i < n; i++) {
                Object key = list.get(i);
                int j = i - 1;
                while (j >= 0 && c.compare(list.get(j), key) > 0) {
                    list.set(j + 1, list.get(j));
                    j--;
                }
                list.set(j + 1, key);
            }
        }
    }
    public Object[] toArray() { return list.toArray(); }
    public <T> T[] toArray(T[] array) { return list.toArray(array); }
    public <T> T[] toArray(Class<T> type) {
        int size = list.size();
        @SuppressWarnings("unchecked")
        T[] array = (T[]) java.lang.reflect.Array.newInstance(type, size);
        for (int i = 0; i < size; i++) {
            array[i] = type.cast(list.get(i));
        }
        return array;
    }
    public void trimToSize() { list.trimToSize(); }
    public static CSharpArrayList repeat(Object value, int count) {
        CSharpArrayList result = new CSharpArrayList(count);
        for (int i = 0; i < count; i++) result.add(value);
        return result;
    }
    public int lastIndexOf(Object value) { return list.lastIndexOf(value); }
    @Override public CSharpArrayList clone() {
        return new CSharpArrayList(list);
    }
}
