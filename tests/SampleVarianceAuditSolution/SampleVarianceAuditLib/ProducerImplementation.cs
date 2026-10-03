namespace SampleVarianceAuditLib;

public sealed class StringProducer : IProducer<string>
{
    private readonly string _value;

    public StringProducer(string value) => _value = value;

    public string Produce() => _value;
}

public sealed class Box<T> : IReadOnlyBox<T>
{
    public Box(T value) => Value = value;

    public T Value { get; }
}

public sealed class StringConsumer : IConsumer<string>
{
    private readonly List<string> _seen = new();

    public void Consume(string item) => _seen.Add(item);

    public int Count => _seen.Count;
}

public sealed class NestedImpl<T> : INested<T>
{
    public void Apply(IEnumerable<Action<T>> actions)
    {
        foreach (var action in actions)
        {
            action(default!);
        }
    }
}