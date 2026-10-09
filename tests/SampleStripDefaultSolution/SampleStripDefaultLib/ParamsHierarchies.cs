namespace SampleStripDefaultLib;

// C48 M2 (PC-8): params slots across the virtual/interface family — the whole
// family must materialize consistently when the rule is enabled.

public abstract class ParamsSinkBase
{
    public abstract string Flush(params IEnumerable<char> chars);

    public virtual string Emit(params IEnumerable<string> parts)
    {
        return string.Join("|", parts);
    }
}

public interface IParamsBatch
{
    string Merge(params IEnumerable<string> parts);
}

public sealed class ParamsConsoleSink : ParamsSinkBase, IParamsBatch
{
    public ParamsConsoleSink()
    {
    }

    public ParamsConsoleSink(int seed, params IEnumerable<int> seeds)
    {
        SeedSum = seed + seeds.Sum();
    }

    public int SeedSum { get; }

    public override string Emit(params IEnumerable<string> parts)
    {
        return "console:" + base.Emit(parts);
    }

    public override string Flush(params IEnumerable<char> chars)
    {
        return new string(chars.ToArray());
    }

    public string Merge(params IEnumerable<string> parts) => string.Join("+", parts);

    string IParamsBatch.Merge(params IEnumerable<string> parts) => "iface:" + Merge(parts);
}
