package dotnet.wpf.swing.media;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Iterator;
import java.util.List;
import java.util.ListIterator;

/**
 * System.Windows.Media.DoubleCollection compatibility class.
 */
public final class DoubleCollection implements List<Double> {

    private final ArrayList<Double> items;

    public DoubleCollection() {
        items = new ArrayList<>();
    }

    public DoubleCollection(int capacity) {
        items = new ArrayList<>(capacity);
    }

    public DoubleCollection(Iterable<Double> collection) {
        items = new ArrayList<>();
        if (collection != null) {
            for (Double d : collection) {
                items.add(d);
            }
        }
    }

    public int getCount() {
        return items.size();
    }

    @Override
    public int size() {
        return items.size();
    }

    @Override
    public boolean isEmpty() {
        return items.isEmpty();
    }

    @Override
    public boolean contains(Object o) {
        return items.contains(o);
    }

    @Override
    public Iterator<Double> iterator() {
        return items.iterator();
    }

    @Override
    public Object[] toArray() {
        return items.toArray();
    }

    @Override
    public <T> T[] toArray(T[] a) {
        return items.toArray(a);
    }

    @Override
    public boolean add(Double value) {
        return items.add(value);
    }

    @Override
    public boolean remove(Object o) {
        return items.remove(o);
    }

    @Override
    public boolean containsAll(Collection<?> c) {
        return items.containsAll(c);
    }

    @Override
    public boolean addAll(Collection<? extends Double> c) {
        return items.addAll(c);
    }

    @Override
    public boolean addAll(int index, Collection<? extends Double> c) {
        return items.addAll(index, c);
    }

    @Override
    public boolean removeAll(Collection<?> c) {
        return items.removeAll(c);
    }

    @Override
    public boolean retainAll(Collection<?> c) {
        return items.retainAll(c);
    }

    @Override
    public void clear() {
        items.clear();
    }

    @Override
    public Double get(int index) {
        return items.get(index);
    }

    @Override
    public Double set(int index, Double element) {
        return items.set(index, element);
    }

    @Override
    public void add(int index, Double element) {
        items.add(index, element);
    }

    @Override
    public Double remove(int index) {
        return items.remove(index);
    }

    @Override
    public int indexOf(Object o) {
        return items.indexOf(o);
    }

    @Override
    public int lastIndexOf(Object o) {
        return items.lastIndexOf(o);
    }

    @Override
    public ListIterator<Double> listIterator() {
        return items.listIterator();
    }

    @Override
    public ListIterator<Double> listIterator(int index) {
        return items.listIterator(index);
    }

    @Override
    public List<Double> subList(int fromIndex, int toIndex) {
        return items.subList(fromIndex, toIndex);
    }
}
