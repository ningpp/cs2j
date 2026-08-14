package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Specialized.OrderedDictionary.
 */
public class OrderedDictionary implements IOrderedDictionary, CSharpIDictionary {
    private final Map<Object, Object> items = new LinkedHashMap<>();

    public int size() {
        return items.size();
    }

    public int getCount() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    public boolean containsKey(Object key) {
        return items.containsKey(key);
    }

    public Object get(Object key) {
        return items.get(key);
    }

    public Object put(Object key, Object value) {
        return items.put(key, value);
    }

    public Set<Object> keySet() {
        return items.keySet();
    }

    public Set<Map.Entry<Object, Object>> entrySet() {
        return items.entrySet();
    }

    @Override
    public CSharpGenericEnumerator<Object> iterator() {
        return CSharpGenericEnumerator.from(items.entrySet().iterator());
    }

    public java.util.Iterator<Map.Entry<Object, Object>> entrySetIterator() {
        return items.entrySet().iterator();
    }

    public void add(Object key, Object value) {
        items.put(key, value);
    }

    public boolean contains(Object key) {
        return items.containsKey(key);
    }

    public void remove(Object key) {
        items.remove(key);
    }

    public void clear() {
        items.clear();
    }

    public boolean getIsFixedSize() {
        return false;
    }

    public boolean getIsReadOnly() {
        return false;
    }

    public boolean getIsSynchronized() {
        return false;
    }

    public Object getSyncRoot() {
        return this;
    }

    public CSharpICollection<?> getKeys() {
        CSharpArrayList list = new CSharpArrayList();
        for (Object k : items.keySet()) {
            list.add(k);
        }
        return CSharpICollection.from(list);
    }

    public CSharpICollection<?> getValues() {
        CSharpArrayList list = new CSharpArrayList();
        for (Object v : items.values()) {
            list.add(v);
        }
        return CSharpICollection.from(list);
    }

    public void copyTo(CSharpArray array, int index) {
        int i = index;
        for (Object k : items.keySet()) {
            array.setValue(k, i++);
        }
    }

    public void copyTo(Object[] array, int index) {
        int i = index;
        for (Object k : items.keySet()) {
            array[i++] = k;
        }
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
