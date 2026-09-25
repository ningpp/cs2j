namespace SampleRecordLib;

// MM-2/MM-4/MM-5：void 方法迁移
public record struct Accumulator(int Total)
{
    public Accumulator Add(int n)
    {
        Total += n;
        return this;
    }
}
