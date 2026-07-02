package io.github.ningpp.compat;

import java.util.ArrayList;

public abstract class CSharpCollectionBase implements CSharpIList {

    protected ArrayList<Object> innerList;

    protected CSharpCollectionBase() {
        this.innerList = new ArrayList<>();
    }

    protected ArrayList<Object> getInnerList() {
        return innerList;
    }

    @Override
    public int getCount() {
        return innerList.size();
    }

    // Template methods for hooks
    protected void onClear() {}
    protected void onClearComplete() {}
    protected void onInsert(int index, Object value) {}
    protected void onInsertComplete(int index, Object value) {}
    protected void onRemove(int index, Object value) {}
    protected void onRemoveComplete(int index, Object value) {}
    protected void onSet(int index, Object oldValue, Object newValue) {}
    protected void onSetComplete(int index, Object oldValue, Object newValue) {}
    protected void onValidate(Object value) {}

    @Override
    public int add(Object value) {
        onValidate(value);
        int index = innerList.size();
        onInsert(index, value);
        innerList.add(value);
        onInsertComplete(index, value);
        return index;
    }

    @Override
    public void clear() {
        onClear();
        innerList.clear();
        onClearComplete();
    }

    @Override
    public boolean contains(Object value) {
        return innerList.contains(value);
    }

    @Override
    public int indexOf(Object value) {
        return innerList.indexOf(value);
    }

    @Override
    public void insert(int index, Object value) {
        onValidate(value);
        onInsert(index, value);
        innerList.add(index, value);
        onInsertComplete(index, value);
    }

    @Override
    public boolean getIsFixedSize() {
        return false;
    }

    @Override
    public boolean getIsReadOnly() {
        return false;
    }

    @Override
    public void remove(Object value) {
        int index = innerList.indexOf(value);
        if (index >= 0) {
            onRemove(index, value);
            innerList.remove(index);
            onRemoveComplete(index, value);
        }
    }

    @Override
    public void removeAt(int index) {
        Object value = innerList.get(index);
        onRemove(index, value);
        innerList.remove(index);
        onRemoveComplete(index, value);
    }

    @Override
    public Object get(int index) {
        return innerList.get(index);
    }

    @Override
    public Object set(int index, Object value) {
        onValidate(value);
        Object oldValue = innerList.get(index);
        onSet(index, oldValue, value);
        innerList.set(index, value);
        onSetComplete(index, oldValue, value);
        return oldValue;
    }

    @Override
    public CSharpEnumerator iterator() {
        return CSharpEnumerator.from(innerList.iterator());
    }

    @Override
    public void copyTo(Object[] array, int index) {
        for (int i = 0; i < innerList.size(); i++) {
            array[index + i] = innerList.get(i);
        }
    }

    @Override
    public boolean getIsSynchronized() {
        return false;
    }

    @Override
    public Object getSyncRoot() {
        return innerList;
    }
}
