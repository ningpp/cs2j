package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.TreeMap;

public class CSharpObjSortedList implements CSharpIDictionary, Cloneable {
    private final TreeMap<Object, Object> map;
    private final Object syncRoot = new Object();

    public CSharpObjSortedList() { this.map = new TreeMap<>(); }
    public CSharpObjSortedList(java.util.Comparator<?> comparer) { this.map = new TreeMap<>((java.util.Comparator<Object>) comparer); }

    @Override public void add(Object key, Object value) { map.put(key, value); }
    @Override public void clear() { map.clear(); }
    @Override public boolean contains(Object key) { return map.containsKey(key); }
    public boolean containsKey(Object key) { return map.containsKey(key); }
    public boolean containsValue(Object value) { return map.containsValue(value); }
    @Override public boolean getIsFixedSize() { return false; }
    @Override public boolean getIsReadOnly() { return false; }
    @Override public CSharpCollection getKeys() {
        return new CSharpCollection() {
            @Override public int size() { return map.size(); }
            @Override public int getCount() { return map.size(); }
            @Override public void copyTo(CSharpArray array, int index) {
                int i = index; for (Object k : map.keySet()) array.setValue(k, i++);
            }
            @Override public boolean getIsSynchronized() { return false; }
            @Override public Object getSyncRoot() { return syncRoot; }
            @Override public CSharpEnumerator iterator() { return CSharpEnumerator.from(map.keySet().iterator()); }
        };
    }
    @Override public CSharpCollection getValues() {
        return new CSharpCollection() {
            @Override public int size() { return map.size(); }
            @Override public int getCount() { return map.size(); }
            @Override public void copyTo(CSharpArray array, int index) {
                int i = index; for (Object v : map.values()) array.setValue(v, i++);
            }
            @Override public boolean getIsSynchronized() { return false; }
            @Override public Object getSyncRoot() { return syncRoot; }
            @Override public CSharpEnumerator iterator() { return CSharpEnumerator.from(map.values().iterator()); }
        };
    }
    @Override public void remove(Object key) { map.remove(key); }
    @Override public Object get(Object key) { return map.get(key); }
    @Override public Object put(Object key, Object value) { return map.put(key, value); }
    @Override public int size() { return map.size(); }
    @Override public int getCount() { return map.size(); }
    @Override public void copyTo(CSharpArray array, int index) {
        int i = index;
        for (Map.Entry<Object, Object> e : map.entrySet()) {
            array.setValue(new CSharpDictEntry(e.getKey(), e.getValue()), i++);
        }
    }
    @Override public boolean getIsSynchronized() { return false; }
    @Override public Object getSyncRoot() { return syncRoot; }
    @Override public CSharpEnumerator iterator() {
        return CSharpEnumerator.from(map.entrySet().stream()
            .map(e -> (Object) new CSharpDictEntry(e.getKey(), e.getValue()))
            .iterator());
    }

    public Object getByIndex(int index) {
        int i = 0;
        for (Object v : map.values()) { if (i++ == index) return v; }
        throw new IndexOutOfBoundsException();
    }
    public Object getKey(int index) {
        int i = 0;
        for (Object k : map.keySet()) { if (i++ == index) return k; }
        throw new IndexOutOfBoundsException();
    }
    public List<Object> getKeyList() { return new ArrayList<>(map.keySet()); }
    public List<Object> getValueList() { return new ArrayList<>(map.values()); }
    public int indexOfKey(Object key) {
        int i = 0;
        for (Object k : map.keySet()) { if (k.equals(key)) return i; i++; }
        return -1;
    }
    public int indexOfValue(Object value) {
        int i = 0;
        for (Object v : map.values()) { if (Objects.equals(v, value)) return i; i++; }
        return -1;
    }
    public void removeAt(int index) { map.remove(getKey(index)); }
    public void setByIndex(int index, Object value) {
        Object key = getKey(index);
        map.put(key, value);
    }
    public int getCapacity() { return map.size(); }
    public void trimToSize() { /* no-op for TreeMap */ }
    @Override public CSharpObjSortedList clone() {
        CSharpObjSortedList c = new CSharpObjSortedList();
        c.map.putAll(this.map);
        return c;
    }
}
