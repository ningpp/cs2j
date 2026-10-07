package dotnet.System.Collections;

/**
 * C40 javac gate runtime: non-generic C# IEnumerable surface.
 */
public interface IEnumerable extends java.lang.Iterable<Object> {
    @Override
    IEnumerator iterator();
}
