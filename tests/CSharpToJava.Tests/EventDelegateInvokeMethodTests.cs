using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for event delegate invocation generating the correct Java SAM method name.
/// Custom delegates should use run/accept/apply/get based on their signature,
/// not hardcoded "invoke" which doesn't exist on Java functional interfaces.
/// </summary>
public class EventDelegateInvokeMethodTests
{
    [Fact]
    public void VoidNoArg_Delegate_Event_UsesRun()
    {
        var source = @"
delegate void MyAction();
class Sample {
    public event MyAction OnDone;
    void Fire() { OnDone?.Invoke(); }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // The fire method should use .run() not .invoke()
        Assert.Contains(".run()", result.GeneratedCode);
        Assert.DoesNotContain(".invoke()", result.GeneratedCode);
    }

    [Fact]
    public void VoidOneArg_Delegate_Event_UsesAccept()
    {
        var source = @"
delegate void MyHandler(int value);
class Sample {
    public event MyHandler OnChanged;
    void Fire() { OnChanged?.Invoke(42); }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains(".accept(", result.GeneratedCode);
        Assert.DoesNotContain(".invoke(", result.GeneratedCode);
    }

    [Fact]
    public void ExplicitEventAccessorCombiningDelegateField_UsesDelegateHelper()
    {
        var source = @"
delegate void MyHandler(object sender, object args);
class Sample {
    private MyHandler _changed;
    public event MyHandler Changed {
        add { _changed += value; }
        remove { _changed -= value; }
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.DelegateHelper;", result.GeneratedCode);
        Assert.Contains("_changed = DelegateHelper.combine(_changed, handler);", result.GeneratedCode);
        Assert.Contains("_changed = DelegateHelper.remove(_changed, handler);", result.GeneratedCode);
        Assert.DoesNotContain("_changed += handler", result.GeneratedCode);
        Assert.DoesNotContain("_changed -= handler", result.GeneratedCode);
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
