import io.github.ningpp.compat.CSharpEnumerator;
import io.github.ningpp.compat.CSharpDictEnumerator;
import io.github.ningpp.compat.CSharpHashtable;

public final class SchemaEnumerator implements CSharpEnumerator {
    private CSharpDictEnumerator _enumerator;
    private boolean _iteratorHasNext = false;

    public SchemaEnumerator(CSharpHashtable table) {
        _enumerator = CSharpDictEnumerator.from(table.iterator());
    }

    public boolean hasNext() {
        if (_iteratorHasNext) return true;
        _iteratorHasNext = moveNext();
        return _iteratorHasNext;
    }
    public Object next() {
        if (!_iteratorHasNext && !moveNext()) throw new java.util.NoSuchElementException();
        _iteratorHasNext = false;
        return getCurrent();
    }
    public Object getCurrent() {
        return this.getCurrent$Class();
    }
    public Schema getCurrent$Class() {
        return new Schema();
    }
    public boolean moveNext() { return _enumerator.moveNext(); }
    public void reset() {
        /* reset unsupported for Java Iterator */
        _iteratorHasNext = false;
    }
}

public class Schema {
}

public class Container {
    public void copyTo(Schema[] array, int index, SchemaEnumerator e) {
        for (; e.moveNext(); ) {
        Schema schema = e.getCurrent();
        if (schema != null) {
        array[index++] = e.getCurrent();
        }
        }
    }
}

