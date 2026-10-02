using System.Collections.Generic;
using System.Linq;

namespace SampleParamsCollectionsLib;

/// <summary>
/// Virtual-slot family for the declaration materialization (PC-8): base
/// virtual/abstract members, an override, an interface with implicit and
/// explicit implementations, and a base constructor chain whose initializer
/// carries a params expansion. The whole family is materialized consistently.
/// </summary>
public abstract class FormatSinkBase
{
    protected FormatSinkBase()
    {
    }

    protected FormatSinkBase(params IEnumerable<int> seeds)
    {
        ReceivedSeeds = string.Join(",", seeds);
    }

    public string? ReceivedSeeds { get; protected set; }

    public virtual string Emit(params IEnumerable<string> parts) => string.Join(",", parts);

    public abstract string Flush(params IEnumerable<char> chars);
}

public sealed class ConsoleSink : FormatSinkBase
{
    public ConsoleSink()
    {
    }

    public ConsoleSink(int seed) : base(seed, seed, seed)
    {
    }

    public override string Emit(params IEnumerable<string> parts) => "console:" + base.Emit(parts);

    public override string Flush(params IEnumerable<char> chars) => new string(chars.ToArray());
}

public interface IBatchSink
{
    string Merge(params IEnumerable<string> parts);
}

public sealed class MemoryBatchSink : IBatchSink
{
    public string Merge(params IEnumerable<string> parts) => string.Join("+", parts);

    string IBatchSink.Merge(params IEnumerable<string> parts) => Merge(parts);
}
