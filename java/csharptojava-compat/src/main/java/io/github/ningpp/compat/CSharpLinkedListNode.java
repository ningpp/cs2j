package io.github.ningpp.compat;

public class CSharpLinkedListNode<T> {
    T value;
    CSharpLinkedListNode<T> next;
    CSharpLinkedListNode<T> prev;
    CSharpLinkedList<T> list;

    public CSharpLinkedListNode(T value) {
        this.value = value;
    }

    CSharpLinkedListNode(T value, CSharpLinkedList<T> list, CSharpLinkedListNode<T> prev, CSharpLinkedListNode<T> next) {
        this.value = value;
        this.list = list;
        this.prev = prev;
        this.next = next;
    }

    public T getValue() {
        return value;
    }

    public void setValue(T value) {
        this.value = value;
    }

    public CSharpLinkedListNode<T> getNext() {
        return next;
    }

    public CSharpLinkedListNode<T> getPrevious() {
        return prev;
    }

    public CSharpLinkedList<T> getList() {
        return list;
    }
}
