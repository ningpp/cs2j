package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Iterator;

public class CSharpKeyedByTypeCollection<T> extends ArrayList<T> {

    public CSharpKeyedByTypeCollection() {
        super();
    }

    public CSharpKeyedByTypeCollection(Collection<? extends T> c) {
        super(c);
    }

    public boolean containsType(Class<? extends T> type) {
        for (T item : this) {
            if (item != null && type.equals(item.getClass())) {
                return true;
            }
        }
        return false;
    }

    @SuppressWarnings("unchecked")
    public T findByType(Class<? extends T> type) {
        for (T item : this) {
            if (item != null && type.equals(item.getClass())) {
                return item;
            }
        }
        return null;
    }

    public CSharpList<T> findAllByType(Class<? extends T> type) {
        CSharpList<T> result = new CSharpList<>();
        for (T item : this) {
            if (item != null && type.equals(item.getClass())) {
                result.add(item);
            }
        }
        return result;
    }

    @SuppressWarnings("unchecked")
    public T removeByType(Class<? extends T> type) {
        Iterator<T> it = iterator();
        while (it.hasNext()) {
            T item = it.next();
            if (item != null && type.equals(item.getClass())) {
                it.remove();
                return item;
            }
        }
        return null;
    }

    public CSharpList<T> removeAllByType(Class<? extends T> type) {
        CSharpList<T> result = new CSharpList<>();
        Iterator<T> it = iterator();
        while (it.hasNext()) {
            T item = it.next();
            if (item != null && type.equals(item.getClass())) {
                result.add(item);
                it.remove();
            }
        }
        return result;
    }

    public int indexOfType(Class<? extends T> type) {
        for (int i = 0; i < size(); i++) {
            T item = get(i);
            if (item != null && type.equals(item.getClass())) {
                return i;
            }
        }
        return -1;
    }
}
