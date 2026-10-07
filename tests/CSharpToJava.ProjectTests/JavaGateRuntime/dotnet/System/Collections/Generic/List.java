package dotnet.System.Collections.Generic;

/**
 * C40 javac gate runtime: concrete C# List<T> surface. Unlike java.util.List
 * this type is instantiable (generated code emits {@code new List<T>()}) and
 * its iterator follows the C# cursor protocol (moveNext/getCurrent) while
 * also satisfying java.lang.Iterable for Java enhanced-for.
 */
public final class List<T> implements IEnumerable<T> {
    private final java.util.ArrayList<T> items = new java.util.ArrayList<>();

    public List() {
    }

    @SuppressWarnings("unchecked")
    public List(int[] values) {
        if (values != null) {
            for (int v : values) {
                items.add((T) Integer.valueOf(v));
            }
        }
    }

    public List(T[] values) {
        if (values != null) {
            java.util.Collections.addAll(items, values);
        }
    }

    public boolean add(T value) {
        return items.add(value);
    }

    public void add(int index, T value) {
        items.add(index, value);
    }

    public T get(int index) {
        return items.get(index);
    }

    public int size() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    public boolean contains(Object value) {
        return items.contains(value);
    }

    public int indexOf(Object value) {
        return items.indexOf(value);
    }

    public boolean remove(Object value) {
        return items.remove(value);
    }

    public void clear() {
        items.clear();
    }

    public Object[] toArray() {
        return items.toArray();
    }

    public <U> U[] toArray(U[] array) {
        return items.toArray(array);
    }

    @Override
    public IEnumerator<T> iterator() {
        return new Enumerator();
    }

    private final class Enumerator implements IEnumerator<T> {
        private int index = -1;

        @Override
        public boolean moveNext() {
            if (index + 1 < items.size()) {
                index++;
                return true;
            }
            return false;
        }

        @Override
        public T getCurrent() {
            return items.get(index);
        }

        @Override
        public void close() {
        }
    }
}
