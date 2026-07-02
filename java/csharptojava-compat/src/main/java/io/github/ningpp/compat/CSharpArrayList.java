package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.Comparator;
import java.util.Iterator;
import java.util.List;
import java.util.Objects;

public class CSharpArrayList implements CSharpIList, Cloneable {
    private final ArrayList<Object> list;
    private final Object syncRoot = new Object();

    public CSharpArrayList() { this.list = new ArrayList<>(); }
    public CSharpArrayList(int capacity) { this.list = new ArrayList<>(capacity); }
    public CSharpArrayList(Collection<?> c) { this.list = new ArrayList<>(c); }

    @Override public int add(Object value) { list.add(value); return list.size() - 1; }
    @Override public void clear() { list.clear(); }
    @Override public boolean contains(Object value) { return list.contains(value); }
    @Override public int indexOf(Object value) { return list.indexOf(value); }
    @Override public void insert(int index, Object value) { list.add(index, value); }
    @Override public boolean getIsFixedSize() { return false; }
    @Override public boolean getIsReadOnly() { return false; }
    @Override public void remove(Object value) { list.remove(value); }
    @Override public void removeAt(int index) { list.remove(index); }
    @Override public Object get(int index) { return list.get(index); }
    @Override public Object set(int index, Object value) { return list.set(index, value); }
    @Override public int size() { return list.size(); }
    @Override public int getCount() { return list.size(); }
    @Override public void copyTo(Object[] array, int index) {
        for (int i = 0; i < list.size(); i++) array[index + i] = list.get(i);
    }
    @Override public boolean getIsSynchronized() { return false; }
    @Override public Object getSyncRoot() { return syncRoot; }
    @Override public CSharpEnumerator iterator() {
        return CSharpEnumerator.from(list.iterator());
    }

    public int getCapacity() { return -1; } // ArrayList doesn't expose capacity
    public void setCapacity(int value) { list.ensureCapacity(value); }
    public void addRange(Collection<?> c) { list.addAll(c); }
    public int binarySearch(Object value) { return Collections.binarySearch(list, value); }
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
    public void sort(Comparator<?> comparer) { list.sort((Comparator<Object>) comparer); }
    public Object[] toArray() { return list.toArray(); }
    public void trimToSize() { list.trimToSize(); }
    public static CSharpArrayList repeat(Object value, int count) {
        CSharpArrayList result = new CSharpArrayList(count);
        for (int i = 0; i < count; i++) result.add(value);
        return result;
    }
    public int lastIndexOf(Object value) { return list.lastIndexOf(value); }
    @Override public CSharpArrayList clone() {
        try { CSharpArrayList c = (CSharpArrayList) super.clone(); return c; }
        catch (CloneNotSupportedException e) { throw new InternalError(); }
    }
}
