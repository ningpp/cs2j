namespace SampleVarianceAuditLib;

/// <summary>Covariant producer contract: T only appears in output positions.</summary>
public interface IProducer<out T>
{
    T Produce();
}

/// <summary>Read-only shaped covariant contract (out T, get-only member).</summary>
public interface IReadOnlyBox<out T>
{
    T Value { get; }
}

/// <summary>Contravariant consumer contract: T only appears in input positions.</summary>
public interface IConsumer<in T>
{
    void Consume(T item);
}

/// <summary>Nested variance composition: IEnumerable&lt;Action&lt;T&gt;&gt; in a parameter
/// position composes Consumer ∘ out(IEnumerable) ∘ in(Action) back to Producer (design D7).</summary>
public interface INested<out T>
{
    void Apply(IEnumerable<Action<T>> actions);
}

/// <summary>Mixed in/out generic delegate (name avoids the System.Converter clash).</summary>
public delegate TResult Transformer<in TInput, out TResult>(TInput input);