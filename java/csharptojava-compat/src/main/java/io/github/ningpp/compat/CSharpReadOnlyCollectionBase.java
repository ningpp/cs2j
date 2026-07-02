package io.github.ningpp.compat;

import java.util.ArrayList;

public abstract class CSharpReadOnlyCollectionBase implements CSharpCollection {

    protected ArrayList<Object> innerList;

    protected CSharpReadOnlyCollectionBase() {
        this.innerList = new ArrayList<>();
    }

    protected ArrayList<Object> getInnerList() {
        return innerList;
    }

    @Override
    public int getCount() {
        return innerList.size();
    }

    @Override
    public CSharpEnumerator iterator() {
        return CSharpEnumerator.from(innerList.iterator());
    }

    public boolean contains(Object value) {
        return innerList.contains(value);
    }

    public int indexOf(Object value) {
        return innerList.indexOf(value);
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
