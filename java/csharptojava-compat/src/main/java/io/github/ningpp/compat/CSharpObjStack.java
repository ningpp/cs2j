package io.github.ningpp.compat;

import java.util.ArrayDeque;
import java.util.NoSuchElementException;

public class CSharpObjStack implements CSharpIterable, Cloneable {
    private final ArrayDeque<Object> stack;
    private final Object syncRoot = new Object();

    public CSharpObjStack() { this.stack = new ArrayDeque<>(); }
    public CSharpObjStack(int capacity) { this.stack = new ArrayDeque<>(capacity); }
    public CSharpObjStack(java.util.Collection<?> c) { this.stack = new ArrayDeque<>(c); }

    public void push(Object obj) { stack.addLast(obj); }
    public Object pop() {
        if (stack.isEmpty()) throw new NoSuchElementException("Stack is empty");
        return stack.removeLast();
    }
    public Object peek() {
        if (stack.isEmpty()) throw new NoSuchElementException("Stack is empty");
        return stack.peekLast();
    }
    public int size() { return stack.size(); }
    public int getCount() { return stack.size(); }
    public boolean contains(Object obj) { return stack.contains(obj); }
    public void clear() { stack.clear(); }
    public void copyTo(Object[] array, int index) {
        int i = index;
        java.util.Iterator<Object> it = stack.descendingIterator();
        while (it.hasNext()) array[i++] = it.next();
    }
    public Object[] toArray() {
        Object[] result = new Object[stack.size()];
        int i = 0;
        java.util.Iterator<Object> it = stack.descendingIterator();
        while (it.hasNext()) result[i++] = it.next();
        return result;
    }
    public Object getSyncRoot() { return syncRoot; }
    public boolean getIsSynchronized() { return false; }
    @Override public CSharpEnumerator iterator() {
        // C# Stack enumerator returns top-to-bottom
        return CSharpEnumerator.from(new java.util.Iterator<Object>() {
            private final java.util.Iterator<Object> it = stack.descendingIterator();
            @Override public boolean hasNext() { return it.hasNext(); }
            @Override public Object next() { return it.next(); }
        });
    }
    @Override public CSharpObjStack clone() {
        try { CSharpObjStack c = (CSharpObjStack) super.clone(); return c; }
        catch (CloneNotSupportedException e) { throw new InternalError(); }
    }
}
