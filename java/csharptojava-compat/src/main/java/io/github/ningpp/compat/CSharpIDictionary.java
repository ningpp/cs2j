package io.github.ningpp.compat;

import java.util.LinkedHashSet;
import java.util.Iterator;
import java.util.Map;
import java.util.Set;

public interface CSharpIDictionary extends CSharpCollection {
    void add(Object key, Object value);
    void clear();
    boolean contains(Object key);
    boolean getIsFixedSize();
    boolean getIsReadOnly();
    /** Default implementations so converter-generated classes that emit getKeys()/
     * getValues() returning CSharpICollection&lt;?&gt; still compile. */
    default CSharpICollection<?> getKeys() {
        CSharpArrayList list = new CSharpArrayList();
        for (Object item : this) {
            if (item instanceof Map.Entry<?, ?> entry) {
                list.add(entry.getKey());
            }
        }
        return CSharpICollection.from(list);
    }
    default CSharpICollection<?> getValues() {
        CSharpArrayList list = new CSharpArrayList();
        for (Object item : this) {
            if (item instanceof Map.Entry<?, ?> entry) {
                list.add(entry.getValue());
            }
        }
        return CSharpICollection.from(list);
    }
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
     * Bridges any object to CSharpIDictionary at runtime. Used when the C# cast
     * target is IDictionary but the source type is object (e.g. ReturnValue).
     * If the object is already a CSharpIDictionary, returns it directly.
     * If it is a CSharpGenericIDictionary, wraps with from().
     * Otherwise throws ClassCastException to match C# semantics.
     */
    @SuppressWarnings({ "unchecked", "rawtypes" })
    static CSharpIDictionary fromAny(Object value) {
        if (value == null) {
            return null;
        }
        if (value instanceof CSharpIDictionary dict) {
            return dict;
        }
        if (value instanceof CSharpGenericIDictionary<?, ?> dict) {
            return from(dict);
        }
        throw new ClassCastException("Cannot cast " + value.getClass().getName() + " to CSharpIDictionary");
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
                return ((CSharpGenericIDictionary) dict).containsKey(key);
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
            public CSharpICollection<?> getKeys() {
                CSharpList<Object> keys = new CSharpList<>();
                for (CSharpKeyValuePair<?, ?> pair : dict) {
                    keys.add(pair.getKey());
                }
                return CSharpICollection.from(asCollection(keys));
            }
            @Override
            public CSharpICollection<?> getValues() {
                CSharpList<Object> values = new CSharpList<>();
                for (CSharpKeyValuePair<?, ?> pair : dict) {
                    values.add(pair.getValue());
                }
                return CSharpICollection.from(asCollection(values));
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
            // size() is a plain helper (CSharpCollection declares only getCount()); both
            // members read the backing dictionary's size.
            public int size() {
                return ((CSharpGenericIDictionary) dict).size();
            }
            @Override
            public int getCount() {
                return ((CSharpGenericIDictionary) dict).size();
            }
            @Override
            public CSharpGenericEnumerator<Object> iterator() {
                return CSharpGenericEnumerator.<Object>from(new Iterator<Object>() {
                    private final Iterator<? extends CSharpKeyValuePair<?, ?>> it = dict.iterator();

                    @Override
                    public boolean hasNext() {
                        return it.hasNext();
                    }

                    @Override
                    public Object next() {
                        CSharpKeyValuePair<?, ?> pair = it.next();
                        return new CSharpDictEntry(pair.getKey(), pair.getValue());
                    }
                });
            }
        };
    }

    private static CSharpCollection asCollection(CSharpList<Object> list) {
        return new CSharpCollection() {
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
