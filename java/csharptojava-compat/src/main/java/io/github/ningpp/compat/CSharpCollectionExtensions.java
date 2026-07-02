package io.github.ningpp.compat;

import java.util.Collection;

public final class CSharpCollectionExtensions {

    private CSharpCollectionExtensions() {
    }

    public static <T> void addRange(CSharpICollection<T> collection, Collection<? extends T> items) {
        for (T item : items) {
            collection.add(item);
        }
    }

    public static <T> void insertRange(CSharpGenericIList<T> list, int index, Collection<? extends T> items) {
        int i = index;
        for (T item : items) {
            list.insert(i++, item);
        }
    }

    public static <K, V> V getOrAdd(CSharpGenericIDictionary<K, V> dict, K key, V value) {
        if (dict.containsKey(key)) {
            return dict.get(key);
        }
        dict.add(key, value);
        return value;
    }

    public static <T> CSharpReadOnlyList<T> asReadOnly(CSharpGenericIList<T> list) {
        return new CSharpReadOnlyList<T>() {
            @Override
            public T get(int index) {
                return list.get(index);
            }

            @Override
            public int getCount() {
                return list.getCount();
            }

            @Override
            public CSharpGenericEnumerator<T> iterator() {
                return list.iterator();
            }
        };
    }
}
