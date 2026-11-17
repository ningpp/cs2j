using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// The ByRef slot boxing rule only applies to instance writes (the value lands in
/// the slot). The static array-alias factory <c>ByRef&lt;T&gt;.Create(T[], int)</c>
/// takes an INDEX as its second argument, so boxing it by the slot type emitted
/// <c>ByRef.create(a, Double.valueOf(i))</c> (javac: no suitable create(double[],
/// Double) overload). Real chain: XCoordsWithAlignment.HorizontalBalancing.
/// </summary>
public class ByRefArrayIndexBoxingTests
{
    private const string ByRefSupport = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create<T>(T[] array, int index) => new ArrayElementByRef<T>(array, index);
                public void SetValue(T value) { }
            }
            internal sealed class ArrayElementByRef<T> : ByRef<T>
            {
                private readonly T[] _array;
                private readonly int _index;
                public ArrayElementByRef(T[] array, int index) { _array = array; _index = index; }
                public override ref T Value => ref _array[_index];
            }
        }
        """;

    [Fact]
    public void ArrayAliasFactory_IntIndex_NotBoxedAsSlotType()
    {
        var result = Convert(ByRefSupport + """

class Sample
{
    static void M(double[] a, int i)
    {
        Cs2jByRef.ByRef<double> r = Cs2jByRef.ByRef<double>.Create<double>(a, i);
        double d = r.Value;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("ByRef.create(", code);
        Assert.Contains(", i)", code);
        Assert.DoesNotContain("Double.valueOf(i)", code);
    }

    [Fact]
    public void InstanceSetter_IntegralValue_StillBoxedAsSlotType()
    {
        var result = Convert(ByRefSupport + """

class Sample
{
    static void M(Cs2jByRef.ByRef<long> holder, int i)
    {
        holder.SetValue(i);
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("setValue(Long.valueOf(i))", result.GeneratedCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
