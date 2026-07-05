package io.github.ningpp.compat;

import java.util.ArrayDeque;
import java.util.Collection;
import java.util.NoSuchElementException;
import java.util.Objects;

public class CSharpStack<T> implements CSharpICollection<T>, Cloneable {
    private ArrayDeque<T> deque;

    public CSharpStack() {
        this.deque = new ArrayDeque<>();
    }

    public CSharpStack(int capacity) {
        this.deque = new ArrayDeque<>(capacity);
    }

    public CSharpStack(Collection<? extends T> c) {
        this.deque = new ArrayDeque<>(c.size());
        for (T item : c) {
            deque.addLast(item);
        }
    }

    public void push(T item) {
        deque.addLast(item);
    }

    public T pop() {
        if (deque.isEmpty()) {
            throw new NoSuchElementException("Stack is empty");
        }
        return deque.removeLast();
    }

    public T peek() {
        if (deque.isEmpty()) {
            throw new NoSuchElementException("Stack is empty");
        }
        return deque.peekLast();
    }

    @Override
    public void clear() {
        deque.clear();
    }

    @Override
    public boolean contains(Object o) {
        return deque.contains(o);
    }

    @Override
    public void copyTo(T[] array, int arrayIndex) {
        Objects.checkIndex(arrayIndex, array.length + 1);
        if (arrayIndex + deque.size() > array.length) {
            throw new IndexOutOfBoundsException(
                "Destination array is not long enough to copy all elements.");
        }
        int i = arrayIndex;
        // C# Stack.CopyTo copies top-to-bottom (LIFO order)
        java.util.Iterator<T> it = deque.descendingIterator();
        while (it.hasNext()) {
            array[i++] = it.next();
        }
    }

    @Override
    public Object[] toArray() {
        Object[] result = new Object[deque.size()];
        int i = 0;
        // C# Stack.ToArray returns top-to-bottom
        java.util.Iterator<T> it = deque.descendingIterator();
        while (it.hasNext()) {
            result[i++] = it.next();
        }
        return result;
    }

    @Override
    @SuppressWarnings("unchecked")
    public <E> E[] toArray(E[] a) {
        int size = deque.size();
        E[] result = a.length >= size
            ? a
            : (E[]) java.lang.reflect.Array.newInstance(a.getClass().getComponentType(), size);
        int i = 0;
        java.util.Iterator<T> it = deque.descendingIterator();
        while (it.hasNext()) {
            result[i++] = (E) it.next();
        }
        if (result.length > size) {
            result[size] = null;
        }
        return result;
    }

    public void trimExcess() {
        ArrayDeque<T> newDeque = new ArrayDeque<>(deque);
        deque = newDeque;
    }

    public int ensureCapacity(int capacity) {
        if (capacity < 0) {
            throw new IllegalArgumentException("capacity must be non-negative");
        }
        if (capacity > deque.size()) {
            ArrayDeque<T> newDeque = new ArrayDeque<>(capacity);
            newDeque.addAll(deque);
            deque = newDeque;
        }
        return capacity;
    }

    public T tryPop() {
        if (deque.isEmpty()) {
            return null;
        }
        return deque.removeLast();
    }

    public T tryPeek() {
        if (deque.isEmpty()) {
            return null;
        }
        return deque.peekLast();
    }

    @Override
    public int getCount() {
        return deque.size();
    }

    @Override
    public boolean getIsReadOnly() {
        return false;
    }

    @Override
    public boolean add(T item) {
        push(item);
        return true;
    }

    @Override
    public boolean remove(Object o) {
        return deque.remove(o);
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        // C# Stack enumerator iterates top-to-bottom (LIFO order)
        return CSharpGenericEnumerator.from(deque.descendingIterator());
    }

    @Override
    @SuppressWarnings("unchecked")
    public CSharpStack<T> clone() {
        try {
            CSharpStack<T> c = (CSharpStack<T>) super.clone();
            c.deque = new ArrayDeque<>(this.deque);
            return c;
        } catch (CloneNotSupportedException e) {
            throw new InternalError();
        }
    }
}
