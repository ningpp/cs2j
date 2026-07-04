package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Objects;

public class CSharpHashtable implements CSharpIDictionary, Cloneable {
    private final LinkedHashMap<Object, Object> map;
    private final Object syncRoot = new Object();

    public CSharpHashtable() { this.map = new LinkedHashMap<>(); }
    public CSharpHashtable(int capacity) { this.map = new LinkedHashMap<>(capacity); }
    public CSharpHashtable(Map<?, ?> m) { this.map = new LinkedHashMap<>(m); }

    @Override public void add(Object key, Object value) { map.put(key, value); }
    @Override public void clear() { map.clear(); }
    @Override public boolean contains(Object key) { return map.containsKey(key); }
    public boolean containsKey(Object key) { return map.containsKey(key); }
    public boolean containsValue(Object value) { return map.containsValue(value); }
    @Override public boolean getIsFixedSize() { return false; }
    @Override public boolean getIsReadOnly() { return false; }
    @Override public CSharpCollection getKeys() { return new CSharpKeyCollection(map.keySet()); }
    @Override public CSharpCollection getValues() { return new CSharpValueCollection(map.values()); }
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
    @Override public CSharpHashtable clone() {
        return new CSharpHashtable(map);
    }

    // Inner helper collections for Keys/Values
    private static class CSharpKeyCollection implements CSharpCollection {
        private final java.util.Set<Object> keys;
        private final Object syncRoot = new Object();
        CSharpKeyCollection(java.util.Set<Object> keys) { this.keys = keys; }
        @Override public int size() { return keys.size(); }
        @Override public int getCount() { return keys.size(); }
        @Override public void copyTo(CSharpArray array, int index) {
            int i = index; for (Object k : keys) array.setValue(k, i++);
        }
        @Override public boolean getIsSynchronized() { return false; }
        @Override public Object getSyncRoot() { return syncRoot; }
        @Override public CSharpEnumerator iterator() { return CSharpEnumerator.from(keys.iterator()); }
    }

    private static class CSharpValueCollection implements CSharpCollection {
        private final java.util.Collection<Object> values;
        private final Object syncRoot = new Object();
        CSharpValueCollection(java.util.Collection<Object> values) { this.values = values; }
        @Override public int size() { return values.size(); }
        @Override public int getCount() { return values.size(); }
        @Override public void copyTo(CSharpArray array, int index) {
            int i = index; for (Object v : values) array.setValue(v, i++);
        }
        @Override public boolean getIsSynchronized() { return false; }
        @Override public Object getSyncRoot() { return syncRoot; }
        @Override public CSharpEnumerator iterator() { return CSharpEnumerator.from(values.iterator()); }
    }
}
