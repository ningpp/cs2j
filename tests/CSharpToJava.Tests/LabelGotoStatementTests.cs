using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LabelGotoStatementTests
{
    [Fact]
    public void Label_ForLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Label_WhileLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: while (true) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: while", result.GeneratedCode);
    }

    [Fact]
    public void Label_DoWhileLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: do { } while (true);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: do", result.GeneratedCode);
    }

    [Fact]
    public void Label_ForeachLoop()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(List<int> list) {
        outer: foreach (var x in list) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Label_Block()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: { int x = 1; }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("target: {", result.GeneratedCode);
    }

    [Fact]
    public void Label_SimpleStatement_WrappedInBlock()
    {
        var result = Convert(@"
class Test {
    void M() {
        int x = 0;
        target: x = 1;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("target: { x = 1; }", result.GeneratedCode);
    }

    [Fact]
    public void Label_NestedLabels()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            inner: for (int j = 0; j < 10; j++) { }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
        Assert.Contains("inner: for", result.GeneratedCode);
    }

    [Fact]
    public void Goto_LoopLabel_Continue()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            if (i == 5) goto outer;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue outer;", result.GeneratedCode);
    }

    [Fact]
    public void Goto_BlockLabel_Break()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: {
            if (true) goto target;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("break target;", result.GeneratedCode);
    }

    [Fact]
    public void Goto_OtherLabel_Break()
    {
        var result = Convert(@"
class Test {
    void M() {
        int x = 0;
        target: x = 1;
        if (true) goto target;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("target: { x = 1; }", result.GeneratedCode);
        Assert.Contains("/* TODO: goto target - cross-scope goto unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void GotoCase_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        switch (x) {
            case 1:
                goto case 2;
            case 2:
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto case - unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void GotoDefault_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        switch (x) {
            case 1:
                goto default;
            default:
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto default - unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_CrossScope_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: { }
        goto target;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto target - cross-scope goto unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_UnknownLabel_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M() {
        goto nonexistent;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto nonexistent - label not found in scope */", result.GeneratedCode);
    }

    [Fact]
    public void Break_Label_Preserved()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            for (int j = 0; j < 10; j++) {
                if (j == 5) break;
            }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Goto_WhileLoop_Continue()
    {
        var result = Convert(@"
class Test {
    void M() {
        restart: while (true) {
            if (System.DateTime.Now.Ticks > 0) goto restart;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue restart;", result.GeneratedCode);
    }

    [Fact]
    public void Label_Registry_ClearedBetweenMethods()
    {
        var result = Convert(@"
class Test {
    void M1() {
        outer: for (int i = 0; i < 10; i++) { }
    }
    void M2() {
        goto outer;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto outer - label not found in scope */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_LabelInsideIf_WithinScope()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: for (int i = 0; i < 10; i++) {
            if (i == 3) {
                goto target;
            }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue target;", result.GeneratedCode);
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
