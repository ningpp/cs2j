package dotnet.System.Collections.Generic;

/**
 * C40 javac gate runtime: C# IEnumerable<T>. The foreach-source lowering calls
 * iterator() and drives the result with moveNext()/getCurrent(); covariant
 * return also makes the stub List<T> usable directly in Java enhanced-for.
 */
public interface IEnumerable<T> extends java.lang.Iterable<T> {
    @Override
    IEnumerator<T> iterator();
}
