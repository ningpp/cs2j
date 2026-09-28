namespace SampleGenericDeepenLib;

public sealed class Latest<T> where T : class
{
    public static T? Value;

    public T Remember(T value)
    {
        Value = value;
        return value;
    }
}

public sealed class Node<T> where T : class
{
    public static readonly Node<T> Empty = new();
    public Node<T>? Next;
    public T? Payload;
}

public sealed class Echo<T> where T : class
{
    public static T Identity(T value) => value;
}
