namespace SampleGenericDeepenLib;

public sealed class HashingProcessor<T> : Processor<T>
{
    public override string Key(T input) => typeof(T).GetHashCode().ToString();
}

public sealed class PlainCodec : ICodec
{
    public string Encode<T>(T value) => typeof(T).Name + ":" + value;
}

public sealed class Outer<T>
{
    public sealed class Inner
    {
        public static string Describe() => typeof(T).Name;
        public string InstanceDescribe() => typeof(T).Name;
    }
}
