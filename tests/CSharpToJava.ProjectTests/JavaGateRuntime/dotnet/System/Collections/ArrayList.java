package dotnet.System.Collections;

/**
 * C40 javac gate runtime: non-generic C# ArrayList surface used by the boxed
 * non-generic duck path ({@code new ArrayList(_values).iterator()}). Named
 * like java.util.ArrayList deliberately; the dotnet wildcard import wins in
 * generated code.
 */
public final class ArrayList implements IEnumerable {
    private final java.util.ArrayList<Object> items = new java.util.ArrayList<>();

    public ArrayList() {
    }

    public ArrayList(Object[] values) {
        if (values != null) {
            java.util.Collections.addAll(items, values);
        }
    }

    public boolean add(Object value) {
        return items.add(value);
    }

    public Object get(int index) {
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

    public void clear() {
        items.clear();
    }

    @Override
    public IEnumerator iterator() {
        return new Enumerator();
    }

    private final class Enumerator implements IEnumerator {
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
        public Object getCurrent() {
            return items.get(index);
        }

        @Override
        public void close() {
        }
    }
}
