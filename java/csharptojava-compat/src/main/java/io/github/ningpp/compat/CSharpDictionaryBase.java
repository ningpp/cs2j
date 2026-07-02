package io.github.ningpp.compat;

public abstract class CSharpDictionaryBase implements CSharpIDictionary {

    protected CSharpHashtable innerHashtable;

    protected CSharpDictionaryBase() {
        this.innerHashtable = new CSharpHashtable();
    }

    protected CSharpHashtable getInnerHashtable() {
        return innerHashtable;
    }

    @Override
    public int getCount() {
        return innerHashtable.getCount();
    }

    // Template methods for hooks
    protected void onClear() {}
    protected void onClearComplete() {}
    protected void onInsert(Object key, Object value) {}
    protected void onInsertComplete(Object key, Object value) {}
    protected void onRemove(Object key, Object value) {}
    protected void onRemoveComplete(Object key, Object value) {}
    protected void onSet(Object key, Object oldValue, Object newValue) {}
    protected void onSetComplete(Object key, Object oldValue, Object newValue) {}
    protected void onValidate(Object key, Object value) {}

    @Override
    public void add(Object key, Object value) {
        onValidate(key, value);
        onInsert(key, value);
        innerHashtable.add(key, value);
        onInsertComplete(key, value);
    }

    @Override
    public void clear() {
        onClear();
        innerHashtable.clear();
        onClearComplete();
    }

    @Override
    public boolean contains(Object key) {
        return innerHashtable.contains(key);
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
    public CSharpCollection getKeys() {
        return innerHashtable.getKeys();
    }

    @Override
    public CSharpCollection getValues() {
        return innerHashtable.getValues();
    }

    @Override
    public void remove(Object key) {
        Object value = innerHashtable.get(key);
        onRemove(key, value);
        innerHashtable.remove(key);
        onRemoveComplete(key, value);
    }

    @Override
    public Object get(Object key) {
        return innerHashtable.get(key);
    }

    @Override
    public Object put(Object key, Object value) {
        onValidate(key, value);
        Object oldValue = innerHashtable.get(key);
        onSet(key, oldValue, value);
        Object result = innerHashtable.put(key, value);
        onSetComplete(key, oldValue, value);
        return result;
    }

    @Override
    public CSharpEnumerator iterator() {
        return innerHashtable.iterator();
    }

    @Override
    public void copyTo(Object[] array, int index) {
        innerHashtable.copyTo(array, index);
    }

    @Override
    public boolean getIsSynchronized() {
        return false;
    }

    @Override
    public Object getSyncRoot() {
        return innerHashtable.getSyncRoot();
    }
}
