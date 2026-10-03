using System.Collections.Generic;

namespace SampleClosedMetadataLib;

/// <summary>
/// C30 closed-metadata fixture DTOs: nested collection members whose closed types the
/// closed-metadata pass registers (CM-ENUM-1), a dynamic-closure对照 (CM-DYN), and two
/// DTOs sharing one closure (dedup).
/// </summary>
public class NestedListDto
{
    public List<List<short>> Matrix { get; set; } = new();

    public Dictionary<string, List<int>> Index { get; set; } = new();
}

public class SharedClosureDtoA
{
    public List<List<short>> Values { get; set; } = new();
}

public class SharedClosureDtoB
{
    public List<List<short>> Values { get; set; } = new();
}

/// <summary>
/// Dynamic-closure对照: Items mentions the unbound T, so the collector reports
/// CS2J1210 (Info) and does not register it (A2 call-site tokens, design CM-DYN).
/// </summary>
public class Bag<T>
{
    public List<T> Items { get; set; } = new();
}
