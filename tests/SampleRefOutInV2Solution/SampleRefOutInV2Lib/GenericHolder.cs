namespace SampleRefOutInV2Lib;

public class GenericHolder<T> where T : struct
{
    public T Value;

    public void Update(ref T value)
    {
        Value = value;
        value = Value;
    }

    public bool TryGet(out T result)
    {
        result = Value;
        return true;
    }
}
