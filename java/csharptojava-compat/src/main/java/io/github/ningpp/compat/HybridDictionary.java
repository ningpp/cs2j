package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * C# System.Collections.Specialized.HybridDictionary: keyed dictionary that
 * switches between ListDictionary (small) and Hashtable (large) internals.
 * Java emission only needs the C#-style surface; a LinkedHashMap backing keeps
 * insertion order (ListDictionary is insertion-ordered, matching the small-N
 * behavior the converted corpus relies on).
 */
public class HybridDictionary implements CSharpIDictionary, Cloneable {
    private final LinkedHashMap<Object, Object> map;
    private final Object syncRoot = new Object();

    public HybridDictionary() { this.map = new LinkedHashMap<>(); }
    public HybridDictionary(int capacity) { this.map = new LinkedHashMap<>(); }
    public HybridDictionary(boolean caseInsensitive) { this.map = new LinkedHashMap<>(); }
    public HybridDictionary(int capacity, boolean caseInsensitive) { this.map = new LinkedHashMap<>(); }

    public HybridDictionary(CSharpIDictionary d) {
        this.map = new LinkedHashMap<>();
        if (d != null) {
            for (Map.Entry<Object, Object> e : d.entrySet()) {
                this.map.put(e.getKey(), e.getValue());
            }
        }
    }

    @Override public void add(Object key, Object value) { map.put(key, value); }
    @Override public void clear() { map.clear(); }
    @Override public boolean contains(Object key) { return map.containsKey(key); }
    public boolean containsKey(Object key) { return map.containsKey(key); }
    public boolean containsValue(Object value) { return map.containsValue(value); }
    @Override public boolean getIsFixedSize() { return false; }
    @Override public boolean getIsReadOnly() { return false; }
    @Override public CSharpICollection<?> getKeys() { return CSharpICollection.from(new CSharpKeyCollection(map.keySet())); }
    @Override public CSharpICollection<?> getValues() { return CSharpICollection.from(new CSharpValueCollection(map.values())); }
    @Override public void remove(Object key) { map.remove(key); }
    @Override public Object get(Object key) { return map.get(key); }
    @Override public Object put(Object key, Object value) { return map.put(key, value); }
    public int size() { return map.size(); }
    @Override public int getCount() { return map.size(); }
    public void copyTo(CSharpArray array, int index) {
        int i = index;
        for (Map.Entry<Object, Object> e : map.entrySet()) {
            array.setValue(new CSharpDictEntry(e.getKey(), e.getValue()), i++);
        }
    }
    @Override public boolean getIsSynchronized() { return false; }
    @Override public Object getSyncRoot() { return syncRoot; }
    @Override public CSharpGenericEnumerator<Object> iterator() {
        return CSharpGenericEnumerator.<Object>from(map.entrySet().stream()
            .map(e -> (Object) new CSharpDictEntry(e.getKey(), e.getValue()))
            .iterator());
    }

    @Override public HybridDictionary clone() {
        HybridDictionary copy = new HybridDictionary();
        copy.map.putAll(this.map);
        return copy;
    }

    private static class CSharpKeyCollection implements CSharpCollection {
        private final java.util.Set<Object> keys;
        private final Object syncRoot = new Object();
        CSharpKeyCollection(java.util.Set<Object> keys) { this.keys = keys; }
        public int size() { return keys.size(); }
        @Override public int getCount() { return keys.size(); }
        public void copyTo(CSharpArray array, int index) {
            int i = index; for (Object k : keys) array.setValue(k, i++);
        }
        @Override public boolean getIsSynchronized() { return false; }
        @Override public Object getSyncRoot() { return syncRoot; }
        @Override public CSharpGenericEnumerator<Object> iterator() { return CSharpGenericEnumerator.<Object>from(keys.iterator()); }
    }

    private static class CSharpValueCollection implements CSharpCollection {
        private final java.util.Collection<Object> values;
        private final Object syncRoot = new Object();
        CSharpValueCollection(java.util.Collection<Object> values) { this.values = values; }
        public int size() { return values.size(); }
        @Override public int getCount() { return values.size(); }
        public void copyTo(CSharpArray array, int index) {
            int i = index; for (Object v : values) array.setValue(v, i++);
        }
        @Override public boolean getIsSynchronized() { return false; }
        @Override public Object getSyncRoot() { return syncRoot; }
        @Override public CSharpGenericEnumerator<Object> iterator() { return CSharpGenericEnumerator.<Object>from(values.iterator()); }
    }
}
