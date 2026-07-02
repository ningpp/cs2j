package io.github.ningpp.compat;

import java.util.Collection;
import java.util.Iterator;
import java.util.NoSuchElementException;
import java.util.Objects;

public class CSharpLinkedList<T> implements CSharpICollection<T>, Cloneable {
    private CSharpLinkedListNode<T> head;
    private CSharpLinkedListNode<T> tail;
    private int count;

    public CSharpLinkedList() {
        this.head = null;
        this.tail = null;
        this.count = 0;
    }

    public CSharpLinkedList(Collection<? extends T> c) {
        this();
        if (c != null) {
            for (T item : c) {
                addLast(item);
            }
        }
    }

    // Node-based operations

    public CSharpLinkedListNode<T> addFirst(T value) {
        CSharpLinkedListNode<T> node = new CSharpLinkedListNode<>(value, this, null, head);
        if (head != null) {
            head.prev = node;
        }
        head = node;
        if (tail == null) {
            tail = node;
        }
        count++;
        return node;
    }

    public void addFirst(CSharpLinkedListNode<T> node) {
        if (node == null) {
            throw new NullPointerException("node");
        }
        if (node.list == this) {
            throw new IllegalArgumentException("Node already belongs to this list");
        }
        if (node.list != null) {
            node.list.remove(node);
        }
        node.list = this;
        node.prev = null;
        node.next = head;
        if (head != null) {
            head.prev = node;
        }
        head = node;
        if (tail == null) {
            tail = node;
        }
        count++;
    }

    public CSharpLinkedListNode<T> addLast(T value) {
        CSharpLinkedListNode<T> node = new CSharpLinkedListNode<>(value, this, tail, null);
        if (tail != null) {
            tail.next = node;
        }
        tail = node;
        if (head == null) {
            head = node;
        }
        count++;
        return node;
    }

    public void addLast(CSharpLinkedListNode<T> node) {
        if (node == null) {
            throw new NullPointerException("node");
        }
        if (node.list == this) {
            throw new IllegalArgumentException("Node already belongs to this list");
        }
        if (node.list != null) {
            node.list.remove(node);
        }
        node.list = this;
        node.prev = tail;
        node.next = null;
        if (tail != null) {
            tail.next = node;
        }
        tail = node;
        if (head == null) {
            head = node;
        }
        count++;
    }

    public CSharpLinkedListNode<T> addBefore(CSharpLinkedListNode<T> node, T value) {
        validateNode(node);
        CSharpLinkedListNode<T> newNode = new CSharpLinkedListNode<>(value, this, node.prev, node);
        if (node.prev != null) {
            node.prev.next = newNode;
        } else {
            head = newNode;
        }
        node.prev = newNode;
        count++;
        return newNode;
    }

    public void addBefore(CSharpLinkedListNode<T> node, CSharpLinkedListNode<T> newNode) {
        validateNode(node);
        if (newNode == null) {
            throw new NullPointerException("node");
        }
        if (newNode.list == this) {
            throw new IllegalArgumentException("Node already belongs to this list");
        }
        if (newNode.list != null) {
            newNode.list.remove(newNode);
        }
        newNode.list = this;
        newNode.prev = node.prev;
        newNode.next = node;
        if (node.prev != null) {
            node.prev.next = newNode;
        } else {
            head = newNode;
        }
        node.prev = newNode;
        count++;
    }

    public CSharpLinkedListNode<T> addAfter(CSharpLinkedListNode<T> node, T value) {
        validateNode(node);
        CSharpLinkedListNode<T> newNode = new CSharpLinkedListNode<>(value, this, node, node.next);
        if (node.next != null) {
            node.next.prev = newNode;
        } else {
            tail = newNode;
        }
        node.next = newNode;
        count++;
        return newNode;
    }

    public void addAfter(CSharpLinkedListNode<T> node, CSharpLinkedListNode<T> newNode) {
        validateNode(node);
        if (newNode == null) {
            throw new NullPointerException("node");
        }
        if (newNode.list == this) {
            throw new IllegalArgumentException("Node already belongs to this list");
        }
        if (newNode.list != null) {
            newNode.list.remove(newNode);
        }
        newNode.list = this;
        newNode.prev = node;
        newNode.next = node.next;
        if (node.next != null) {
            node.next.prev = newNode;
        } else {
            tail = newNode;
        }
        node.next = newNode;
        count++;
    }

    // CSharpICollection<T> methods

    @Override
    public boolean add(T item) {
        addLast(item);
        return true;
    }

    @Override
    public void clear() {
        // Invalidate all nodes
        CSharpLinkedListNode<T> current = head;
        while (current != null) {
            CSharpLinkedListNode<T> next = current.next;
            current.list = null;
            current.prev = null;
            current.next = null;
            current = next;
        }
        head = null;
        tail = null;
        count = 0;
    }

    @Override
    public boolean contains(Object o) {
        return find(o) != null;
    }

    @Override
    public void copyTo(T[] array, int arrayIndex) {
        Objects.checkIndex(arrayIndex, array.length + 1);
        if (arrayIndex + count > array.length) {
            throw new IndexOutOfBoundsException(
                "Destination array is not long enough to copy all elements.");
        }
        int i = arrayIndex;
        for (CSharpLinkedListNode<T> node = head; node != null; node = node.next) {
            array[i++] = node.value;
        }
    }

    @Override
    public boolean remove(Object o) {
        CSharpLinkedListNode<T> node = find(o);
        if (node != null) {
            removeNode(node);
            return true;
        }
        return false;
    }

    public void remove(CSharpLinkedListNode<T> node) {
        validateNode(node);
        removeNode(node);
    }

    public void removeFirst() {
        if (head == null) {
            throw new NoSuchElementException("LinkedList is empty");
        }
        removeNode(head);
    }

    public void removeLast() {
        if (tail == null) {
            throw new NoSuchElementException("LinkedList is empty");
        }
        removeNode(tail);
    }

    @Override
    public int getCount() {
        return count;
    }

    @Override
    public boolean getIsReadOnly() {
        return false;
    }

    public CSharpLinkedListNode<T> getFirst() {
        return head;
    }

    public CSharpLinkedListNode<T> getLast() {
        return tail;
    }

    public CSharpLinkedListNode<T> find(Object o) {
        for (CSharpLinkedListNode<T> node = head; node != null; node = node.next) {
            if (Objects.equals(node.value, o)) {
                return node;
            }
        }
        return null;
    }

    public CSharpLinkedListNode<T> findLast(Object o) {
        for (CSharpLinkedListNode<T> node = tail; node != null; node = node.prev) {
            if (Objects.equals(node.value, o)) {
                return node;
            }
        }
        return null;
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(new Iterator<T>() {
            private CSharpLinkedListNode<T> current = head;

            @Override
            public boolean hasNext() {
                return current != null;
            }

            @Override
            public T next() {
                if (current == null) {
                    throw new NoSuchElementException();
                }
                T val = current.value;
                current = current.next;
                return val;
            }
        });
    }

    @Override
    @SuppressWarnings("unchecked")
    public CSharpLinkedList<T> clone() {
        try {
            CSharpLinkedList<T> c = (CSharpLinkedList<T>) super.clone();
            c.head = null;
            c.tail = null;
            c.count = 0;
            for (CSharpLinkedListNode<T> node = head; node != null; node = node.next) {
                c.addLast(node.value);
            }
            return c;
        } catch (CloneNotSupportedException e) {
            throw new InternalError();
        }
    }

    // Internal helpers

    private void removeNode(CSharpLinkedListNode<T> node) {
        if (node.prev != null) {
            node.prev.next = node.next;
        } else {
            head = node.next;
        }
        if (node.next != null) {
            node.next.prev = node.prev;
        } else {
            tail = node.prev;
        }
        node.prev = null;
        node.next = null;
        node.list = null;
        count--;
    }

    private void validateNode(CSharpLinkedListNode<T> node) {
        if (node == null) {
            throw new NullPointerException("node");
        }
        if (node.list != this) {
            throw new IllegalArgumentException("Node does not belong to this list");
        }
    }

}
