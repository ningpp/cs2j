namespace CSharpToJava.Core.Context;

public enum LabelKind
{
    Loop,
    Block,
    Other
}

public record LabelInfo(string Name, LabelKind Kind);

public class LabelRegistry
{
    private readonly Dictionary<string, LabelInfo> _labels = new(StringComparer.Ordinal);

    public void Register(string name, LabelKind kind)
    {
        _labels[name] = new LabelInfo(name, kind);
    }

    public bool Contains(string name) => _labels.ContainsKey(name);

    public bool TryGetLabel(string name, out LabelInfo? info)
        => _labels.TryGetValue(name, out info);

    public void Clear() => _labels.Clear();
}
