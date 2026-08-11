using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that parameterless C# anonymous methods (delegate { ... }) are emitted as
/// Java lambdas whose parameter list is synthesized from the target delegate-like
/// type's signature, even when the cast target is an unresolved (TypeKind.Error)
/// type that can only be recovered from the compat layer.
/// </summary>
public class AnonymousMethodParameterTests
{
    /// <summary>
    /// Cast target resolves to a real delegate type ? parameters must be synthesized.
    /// </summary>
    [Fact]
    public void AnonymousMethod_ResolvedDelegateCast_SynthesizesParameters()
    {
        var result = Convert(@"
using System;
class T {
    public delegate void RoutedEventHandler(object sender, EventArgs e);
    void M() {
        RoutedEventHandler h = (RoutedEventHandler)(delegate { Console.WriteLine(""x""); });
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("(sender, e) ->", result.GeneratedCode);
    }

    /// <summary>
    /// The cast type cannot be bound (TypeKind.Error, e.g. WPF RoutedEventHandler in a
    /// compilation without WindowsDesktop references) ? the converter must still recover
    /// the arity from the mapped compat functional interface.
    /// </summary>
    [Fact]
    public void AnonymousMethod_UnresolvedCastType_SynthesizesTwoParams()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        RoutedEventHandler h = (RoutedEventHandler)(delegate { Console.WriteLine(""x""); });
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("(sender, e) ->", result.GeneratedCode);
        Assert.DoesNotContain("(RoutedEventHandler)(() ->", result.GeneratedCode);
    }

    /// <summary>
    /// A zero-parameter target delegate must still produce an empty parameter list.
    /// </summary>
    [Fact]
    public void AnonymousMethod_ZeroParamDelegate_ProducesEmptyParams()
    {
        var result = Convert(@"
using System;
class T {
    public delegate void VoidAction();
    void M() {
        VoidAction a = (VoidAction)(delegate { Console.WriteLine(""x""); });
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("() ->", result.GeneratedCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
