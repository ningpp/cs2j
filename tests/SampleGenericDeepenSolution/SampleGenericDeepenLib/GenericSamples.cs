namespace SampleGenericDeepenLib;

public sealed class TypeTokenCache<T> where T : class
{
    private readonly global::System.Collections.Generic.Dictionary<global::System.Type, string> _names = new();

    public string NameOf()
    {
        var t = typeof(T);
        if (!_names.TryGetValue(t, out var n))
        {
            _names[t] = n = t.Name;
        }
        return n;
    }

    public bool IsMine(object? value) => value is T;

    public T? AsMine(object? value) => value as T;

    public string Describe(object? value)
    {
        if (value is T typed)
        {
            return "typed:" + typed.ToString();
        }
        return "other";
    }
}

public static class Guard
{
    public static string DescribeError<TException>(global::System.Action action)
        where TException : global::System.Exception
    {
        try
        {
            action();
            return "ok";
        }
        catch (TException ex)
        {
            return "caught:" + ex.Message;
        }
    }
}
