using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# IEquatable&lt;T&gt;.Equals(T) implementations are mapped to Java's
/// IEquatable.equalsTo(T) so the generated class satisfies the interface contract.
/// </summary>
public class IEquatableMappingTests
{
    [Fact]
    public void ClassImplementingIEquatable_GeneratesEqualsToMethod()
    {
        var result = Convert(@"
using System;
public class Atom : IEquatable<Atom>
{
    private int _value;

    public override bool Equals(object other)
    {
        return Equals(other as Atom);
    }

    public bool Equals(Atom other)
    {
        return _value == other._value;
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("implements IEquatable<Atom>", result.GeneratedCode);
        Assert.Contains("public boolean equalsTo(Atom other)", result.GeneratedCode);
        Assert.DoesNotContain("public boolean equals(Atom other)", result.GeneratedCode);
    }

    [Fact]
    public void IEquatableEqualsCall_MapsToEqualsTo()
    {
        var result = Convert(@"
using System;
public class Atom : IEquatable<Atom>
{
    public bool Equals(Atom other) => true;

    public static bool Same(Atom a, Atom b) => a.Equals(b);
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("a.equalsTo(b)", result.GeneratedCode);
        Assert.DoesNotContain("a.equals(b)", result.GeneratedCode);
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
