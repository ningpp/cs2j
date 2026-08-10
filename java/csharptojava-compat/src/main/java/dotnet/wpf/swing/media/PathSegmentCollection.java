package dotnet.wpf.swing.media;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Iterator;
import java.util.List;
import java.util.ListIterator;

/**
 * System.Windows.Media.PathSegmentCollection compatibility class.
 */
public final class PathSegmentCollection implements List<PathSegment> {

    private final List<PathSegment> items = new ArrayList<>();

    public PathSegmentCollection() {
    }

    public PathSegmentCollection(Iterable<PathSegment> collection) {
        if (collection != null) {
            for (PathSegment s : collection) {
                items.add(s);
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
    public Iterator<PathSegment> iterator() {
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
    public boolean add(PathSegment value) {
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
    public boolean addAll(Collection<? extends PathSegment> c) {
        return items.addAll(c);
    }

    @Override
    public boolean addAll(int index, Collection<? extends PathSegment> c) {
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
    public PathSegment get(int index) {
        return items.get(index);
    }

    @Override
    public PathSegment set(int index, PathSegment element) {
        return items.set(index, element);
    }

    @Override
    public void add(int index, PathSegment element) {
        items.add(index, element);
    }

    @Override
    public PathSegment remove(int index) {
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
    public ListIterator<PathSegment> listIterator() {
        return items.listIterator();
    }

    @Override
    public ListIterator<PathSegment> listIterator(int index) {
        return items.listIterator(index);
    }

    @Override
    public List<PathSegment> subList(int fromIndex, int toIndex) {
        return items.subList(fromIndex, toIndex);
    }
}
