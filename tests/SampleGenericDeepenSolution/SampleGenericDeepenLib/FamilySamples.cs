namespace SampleGenericDeepenLib;

public abstract class Processor<T>
{
    public abstract string Key(T input);
}

public interface ICodec
{
    string Encode<T>(T value);
}
