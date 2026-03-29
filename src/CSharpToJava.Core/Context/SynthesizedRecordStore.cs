using CSharpToJava.Core.Transformers;

namespace CSharpToJava.Core.Context;

/// <summary>
/// Per-file store for synthesized Java record definitions generated from C# anonymous types.
/// Extracted from ConversionContext.
/// </summary>
public class SynthesizedRecordStore
{
    private readonly Dictionary<string, SynthesizedRecordInfo> _records = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recordNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<SynthesizedRecordInfo> Records => _records.Values;

    public bool TryGet(string structuralKey, out SynthesizedRecordInfo? record)
    {
        return _records.TryGetValue(structuralKey, out record);
    }

    public void Register(SynthesizedRecordInfo record)
    {
        var name = record.RecordName;
        if (_recordNames.Contains(name))
        {
            int suffix = 2;
            while (_recordNames.Contains(name + suffix))
                suffix++;
            name = name + suffix;
            record = new SynthesizedRecordInfo(name, record.Fields, record.StructuralKey);
        }
        _recordNames.Add(name);
        _records[record.StructuralKey] = record;
    }

    public void Clear()
    {
        _records.Clear();
        _recordNames.Clear();
    }
}
