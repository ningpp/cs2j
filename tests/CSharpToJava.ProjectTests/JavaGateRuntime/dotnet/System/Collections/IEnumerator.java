package dotnet.System.Collections;

/**
 * C40 javac gate runtime: non-generic C# IEnumerator surface
 * (moveNext/getCurrent/close) used by the boxed non-generic foreach path.
 */
public interface IEnumerator extends java.util.Iterator<Object>, AutoCloseable {
    boolean moveNext();

    Object getCurrent();

    @Override
    void close();

    @Override
    default boolean hasNext() {
        return moveNext();
    }

    @Override
    default Object next() {
        return getCurrent();
    }
}
