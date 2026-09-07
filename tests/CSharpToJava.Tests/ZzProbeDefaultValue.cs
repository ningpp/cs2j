using System.Reflection;
using System.Runtime.InteropServices;

namespace CSharpToJava.Tests;

public class ZzProbeDefaultValue
{
    private class Probe
    {
        public Probe(int x, [DefaultParameterValue(50)] int z) => Z = z;
        public int Z { get; }
    }

    private class Probe2
    {
        public Probe2(int x, [Optional, DefaultParameterValue(50)] int z) => Z = z;
        public int Z { get; }
    }

    private class Probe3
    {
        public Probe3(int x, Guid g = default) => G = g;
        public Guid G { get; }
    }

    [Fact]
    public void Probe_DefaultParameterValueOnly()
    {
        var p = typeof(Probe).GetConstructors()[0].GetParameters()[1];
        Assert.False(p.IsOptional);
        Assert.True(p.HasDefaultValue);
        Assert.Equal(50, p.DefaultValue);
        Assert.Equal(0, new Probe(1, 0).Z);
    }

    [Fact]
    public void Probe_OptionalAndDefault()
    {
        var p = typeof(Probe2).GetConstructors()[0].GetParameters()[1];
        Assert.True(p.IsOptional);
        Assert.True(p.HasDefaultValue);
        Assert.Equal(50, p.DefaultValue);
    }

    [Fact]
    public void Probe_DefaultLiteralOnStruct()
    {
        var p = typeof(Probe3).GetConstructors()[0].GetParameters()[1];
        Assert.True(p.IsOptional);
        Assert.True(p.HasDefaultValue);
        Assert.Null(p.DefaultValue);
    }

    // If this compiles, [DefaultParameterValue] alone would make the parameter
    // optional, which would defeat the purpose. It must NOT compile.
    // public void MustNotCompile() { var x = new Probe(1); }
}
