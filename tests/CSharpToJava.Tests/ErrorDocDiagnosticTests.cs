using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;
using System;

namespace CSharpToJava.Tests;

public class ErrorDocDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public ErrorDocDiagnosticTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src, string file = "Sample.cs")
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = file,
            Options = new ConversionOptions(),
        });
    }

    // Error 09: MemberwiseClone mapping
    [Fact]
    public void Error09_MemberwiseClone_MappedToClone()
    {
        var r = Convert(@"
class Settings {
    public int X;
    public virtual Settings Clone() {
        return (Settings)MemberwiseClone();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // The converter should add a memberwiseClone() helper that wraps super.clone()
        // and the class should implement Cloneable
        Assert.Contains("Cloneable", code);
        Assert.Contains("protected Object memberwiseClone()", code);
        Assert.Contains("super.clone()", code);
    }

    // Error 22: Math.Sign → Integer.signum (not yet fixed)
    [Fact(Skip = "Awaiting fix")]
    public void Error22_MathSign_MapsCorrectly()
    {
        var r = Convert(@"
using System;
class Sample {
    int M(int a, int b) {
        return Math.Sign(b - a);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should use Integer.signum for int input or (int)Math.signum
        bool usesIntegerSignum = code.Contains("Integer.signum");
        bool usesCastSignum = code.Contains("(int)") && code.Contains("Math.signum");
        Assert.True(usesIntegerSignum || usesCastSignum,
            $"Should map Math.Sign(int) correctly. Got: {code}");
    }

    // Error 23: !collection.Any() operator precedence
    [Fact]
    public void Error23_NotAny_CorrectPrecedence()
    {
        var r = Convert(@"
using System.Linq;
using System.Collections.Generic;
class Sample {
    bool M(List<int> items) {
        if (!items.Any()) return true;
        return false;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should NOT produce !items.size() > 0 or !items.length > 0
        Assert.DoesNotContain("!items.size() > 0", code);
        Assert.DoesNotContain("!items.length > 0", code);
    }
}
