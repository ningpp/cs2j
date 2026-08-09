package io.github.ningpp.compat;

import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;

public interface CSharpIDictionary extends CSharpCollection {
    void add(Object key, Object value);
    void clear();
    boolean contains(Object key);
    boolean getIsFixedSize();
    boolean getIsReadOnly();
    CSharpCollection getKeys();
    CSharpCollection getValues();
    void remove(Object key);
    Object get(Object key);
    Object put(Object key, Object value);

    default Set<Map.Entry<Object, Object>> entrySet() {
        LinkedHashSet<Map.Entry<Object, Object>> entries = new LinkedHashSet<>();
        for (Object item : this) {
            if (item instanceof Map.Entry<?, ?> entry) {
                entries.add(new CSharpDictEntry(entry.getKey(), entry.getValue()));
            }
        }
        return entries;
    }

    /**
     * Adapts a C# Dictionary&lt;K,V&gt; (CSharpGenericIDictionary in Java) to the
     * non-generic C# IDictionary surface (CSharpIDictionary). C# allows passing a
     * Dictionary to an IDictionary parameter implicitly; the Java interfaces are
     * unrelated, so the converter wraps the argument with this bridge.
     */
    @SuppressWarnings({ "unchecked", "rawtypes" })
    static CSharpIDictionary from(CSharpGenericIDictionary<?, ?> dict) {
        if (dict == null) {
            return null;
        }
        return new CSharpIDictionary() {
            @Override
            public void copyTo(CSharpArray array, int index) {
                throw new UnsupportedOperationException();
            }
            @Override
            public void add(Object key, Object value) {
                ((CSharpGenericIDictionary) dict).put(key, value);
            }
            @Override
            public void clear() {
                dict.clear();
            }
            @Override
            public boolean contains(Object key) {
                return dict.containsKey(key);
            }
            @Override
            public boolean getIsFixedSize() {
                return false;
            }
            @Override
            public boolean getIsReadOnly() {
                return dict.getIsReadOnly();
            }
            @Override
            public boolean getIsSynchronized() {
                return false;
            }
            @Override
            public Object getSyncRoot() {
                return dict;
            }
            @Override
            public CSharpCollection getKeys() {
                CSharpList<Object> keys = new CSharpList<>();
                for (CSharpKeyValuePair<?, ?> pair : dict) {
                    keys.add(pair.getKey());
                }
                return asCollection(keys);
            }
            @Override
            public CSharpCollection getValues() {
                CSharpList<Object> values = new CSharpList<>();
                for (CSharpKeyValuePair<?, ?> pair : dict) {
                    values.add(pair.getValue());
                }
                return asCollection(values);
            }
            @Override
            public void remove(Object key) {
                ((CSharpGenericIDictionary) dict).remove(key);
            }
            @Override
            public Object get(Object key) {
                return ((CSharpGenericIDictionary) dict).get(key);
            }
            @Override
            public Object put(Object key, Object value) {
                return ((CSharpGenericIDictionary) dict).put(key, value);
            }
            @Override
            public CSharpGenericEnumerator<Object> iterator() {
                return CSharpGenericEnumerator.<Object>from(dict.iterator());
            }
        };
    }

    private static CSharpCollection asCollection(CSharpList<Object> list) {
        return new CSharpCollection() {
            @Override
            public void copyTo(CSharpArray array, int index) {
                list.copyTo(array, index);
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
                return list.size();
            }
            @Override
            public int size() {
                return list.size();
            }
            @Override
            public CSharpGenericEnumerator<Object> iterator() {
                return CSharpGenericEnumerator.<Object>from(list.iterator());
            }
        };
    }
}
