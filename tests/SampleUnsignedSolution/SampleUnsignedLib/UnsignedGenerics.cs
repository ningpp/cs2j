using System.Collections.Generic;

namespace SampleUnsignedLib;

public sealed class UnsignedGenerics
{
    public List<uint> List = new List<uint>();
    public Dictionary<uint, ulong> Dict = new Dictionary<uint, ulong>();
    public IReadOnlyCollection<byte> Bytes = new List<byte>();
    public GenericHolder<uint> Holder = new GenericHolder<uint>();
}

public sealed class GenericHolder<T>
{
    public T? Value;
}
