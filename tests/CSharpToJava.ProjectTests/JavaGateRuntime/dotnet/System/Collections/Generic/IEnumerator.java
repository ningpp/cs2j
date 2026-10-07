package dotnet.System.Collections.Generic;

/**
 * C40 javac gate runtime: C# IEnumerator<T> surface used by foreach-source
 * lowering (moveNext/getCurrent/close). Extends java.util.Iterator so the
 * same cursor can back Java enhanced-for over the stub List<T>.
 */
public interface IEnumerator<T> extends java.util.Iterator<T>, AutoCloseable {
    boolean moveNext();

    T getCurrent();

    @Override
    void close();

    @Override
    default boolean hasNext() {
        return moveNext();
    }

    @Override
    default T next() {
        return getCurrent();
    }
}
