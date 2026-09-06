using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that captured variables WRITTEN inside a hoisted local function keep C#
/// by-reference capture semantics (holder parameter + write-back at call sites),
/// instead of silently losing the mutation on a by-value parameter.
/// </summary>
public class LocalFunctionWrittenCaptureTests
{
    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void WrittenCapture_UsesHolderParameterAndWriteBack()
    {
        var result = Convert(@"
class C {
    int M() {
        int best = -1;
        void Search(int v) {
            if (v > best) best = v;
            if (v > 0) Search(v - 1);
        }
        Search(3);
        return best;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // Hoisted signature: written capture becomes a holder parameter.
        Assert.Contains("private void search(int v, io.github.ningpp.compat.IntHolder _cs2j_cap_best)", code, StringComparison.Ordinal);
        // Body references go through holder.value; recursive call passes the holder.
        Assert.Contains("_cs2j_cap_best.value = v;", code, StringComparison.Ordinal);
        Assert.Contains("if (v > _cs2j_cap_best.value)", code, StringComparison.Ordinal);
        Assert.Contains("search(v - 1, _cs2j_cap_best)", code, StringComparison.Ordinal);
        // Top-level call site: holder initialized with current value, write-back after.
        Assert.Contains("new io.github.ningpp.compat.IntHolder(best)", code, StringComparison.Ordinal);
        Assert.Contains("search(3, _bestHolder1)", code, StringComparison.Ordinal);
        Assert.Contains("best = _bestHolder1.value;", code, StringComparison.Ordinal);
    }

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void ReadOnlyCapture_StaysByValue()
    {
        var result = Convert(@"
class C {
    int M(int limit) {
        int total = 0;
        void Add(int v) { total = total; }
        int ReadOnly() { return limit + 1; }
        Add(1);
        return ReadOnly() + total;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // A written capture (total) gets a holder; a read-only capture (limit) stays by value.
        Assert.Contains("private void add(int v, io.github.ningpp.compat.IntHolder _cs2j_cap_total)", code, StringComparison.Ordinal);
        Assert.Contains("private int readOnly(int _cs2j_cap_limit)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ObjectHolder", code, StringComparison.Ordinal);
    }

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void TransitiveWrittenCapture_PropagatesIsWrittenToCaller()
    {
        // Outer captures 'flag' read-only, but calls Inner which writes 'flag'.
        // The fixpoint must propagate IsWritten=true from Inner to Outer so
        // both hoisted signatures use a holder parameter and the call site
        // from Outer to Inner passes the holder (not the bare value).
        var result = Convert(@"
class C {
    int M() {
        int flag = 0;
        void Inner() { flag = 1; }
        void Outer() { int x = flag; Inner(); }
        Outer();
        return flag;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // Inner: writes flag → holder parameter
        Assert.Contains("private void inner(io.github.ningpp.compat.IntHolder _cs2j_cap_flag)", code, StringComparison.Ordinal);
        // Outer: reads flag but calls Inner which writes it → must also use holder
        Assert.Contains("private void outer(io.github.ningpp.compat.IntHolder _cs2j_cap_flag)", code, StringComparison.Ordinal);
        // Outer's body references flag through holder.value
        Assert.Contains("_cs2j_cap_flag.value", code, StringComparison.Ordinal);
        // Outer calls Inner, passing the holder (not a bare value)
        Assert.Contains("inner(_cs2j_cap_flag)", code, StringComparison.Ordinal);
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
