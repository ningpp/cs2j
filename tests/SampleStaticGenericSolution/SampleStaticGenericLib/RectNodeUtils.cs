namespace SampleStaticGenericLib;

/// <summary>
/// A recursive generic helper that forwards its own type parameters in
/// explicit generic calls. The method body has no typeof(T)/default(T), so
/// the Java signature must NOT gain Class<T> leading parameters, and call
/// sites must NOT prepend Class literals.
/// </summary>
public class RectNode<T, P>
{
    public RectNode<T, P>? Left;
    public T Data;
}

public static class RectNodeUtils
{
    public static void Cross<T, P>(RectNode<T, P> a, RectNode<T, P> b)
    {
        if (a.Left != null)
        {
            Cross<T, P>(a.Left, b);
        }
    }
}
