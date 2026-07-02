package io.github.ningpp.compat;

import java.util.LinkedList;
import java.util.NoSuchElementException;

public class CSharpObjQueue implements CSharpIterable, Cloneable {
    private final LinkedList<Object> queue = new LinkedList<>();
    private final Object syncRoot = new Object();

    public CSharpObjQueue() {}
    public CSharpObjQueue(java.util.Collection<?> c) { queue.addAll(c); }

    public void enqueue(Object obj) { queue.addLast(obj); }
    public Object dequeue() {
        if (queue.isEmpty()) throw new NoSuchElementException("Queue is empty");
        return queue.removeFirst();
    }
    public Object peek() {
        if (queue.isEmpty()) throw new NoSuchElementException("Queue is empty");
        return queue.peekFirst();
    }
    public int size() { return queue.size(); }
    public int getCount() { return queue.size(); }
    public boolean contains(Object obj) { return queue.contains(obj); }
    public void clear() { queue.clear(); }
    public void copyTo(Object[] array, int index) {
        for (int i = 0; i < queue.size(); i++) array[index + i] = queue.get(i);
    }
    public Object[] toArray() { return queue.toArray(); }
    public Object getSyncRoot() { return syncRoot; }
    public boolean getIsSynchronized() { return false; }
    @Override public CSharpEnumerator iterator() { return CSharpEnumerator.from(queue.iterator()); }
    @Override public CSharpObjQueue clone() {
        try { CSharpObjQueue c = (CSharpObjQueue) super.clone(); return c; }
        catch (CloneNotSupportedException e) { throw new InternalError(); }
    }
}
