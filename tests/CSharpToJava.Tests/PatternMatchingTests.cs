using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PatternMatchingTests
{
    // ═══════════════════════════════════════════════════════════
    //  is-pattern: not, and, or, relational
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void IsPattern_NotNull_ProducesNotEquals()
    {
        var result = Convert(@"
class C {
    bool Test(object o) => o is not null;
}");
        Assert.True(result.Success);
        Assert.Contains("!(o == null)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPattern_AndPattern_ProducesLogicalAnd()
    {
        var result = Convert(@"
class C {
    bool Test(int x) => x is > 0 and < 100;
}");
        Assert.True(result.Success);
        Assert.Contains("x > 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("&&", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("x < 100", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPattern_OrPattern_ProducesLogicalOr()
    {
        var result = Convert(@"
class C {
    bool Test(int x) => x is 1 or 2 or 3;
}");
        Assert.True(result.Success);
        Assert.Contains("||", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPattern_TypePattern_ProducesInstanceof()
    {
        var result = Convert(@"
class C {
    bool Test(object o) => o is string;
}");
        Assert.True(result.Success);
        Assert.Contains("o instanceof String", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPattern_DeclarationPattern_ProducesInstanceofWithVar()
    {
        var result = Convert(@"
class C {
    void Test(object o) {
        if (o is string s) {
            System.Console.WriteLine(s);
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("o instanceof String s", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPattern_VarPattern_AssignsVar()
    {
        var result = Convert(@"
class C {
    void Test(object o) {
        if (o is var x) {
            System.Console.WriteLine(x);
        }
    }
}");
        Assert.True(result.Success);
        // var pattern always matches, assigns variable
        Assert.Contains("x = o", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════
    //  Switch expression patterns
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SwitchExpr_NotPattern_Negation()
    {
        var result = Convert(@"
class C {
    string Test(object o) => o switch {
        not null => ""has value"",
        _ => ""null""
    };
}");
        Assert.True(result.Success);
        Assert.Contains("o == null", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("!", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchExpr_TypePattern_Instanceof()
    {
        var result = Convert(@"
class C {
    string Test(object o) => o switch {
        string => ""string"",
        int => ""int"",
        _ => ""other""
    };
}");
        Assert.True(result.Success);
        Assert.Contains("instanceof String", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchExpr_RelationalPattern_Comparison()
    {
        var result = Convert(@"
class C {
    string Test(int x) => x switch {
        > 0 => ""positive"",
        < 0 => ""negative"",
        _ => ""zero""
    };
}");
        Assert.True(result.Success);
        Assert.Contains("x > 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("x < 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchExpr_DeclarationPattern_InstanceofBinding()
    {
        var result = Convert(@"
class C {
    string Test(object o) => o switch {
        string s => s.ToUpper(),
        int i => i.ToString(),
        _ => ""unknown""
    };
}");
        Assert.True(result.Success);
        Assert.Contains("o instanceof String s", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("o instanceof int i", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════
    //  Switch statement with CasePatternSwitchLabel => if-else
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SwitchStmt_PatternCase_DeclarationType_ConvertsToIfElse()
    {
        var result = Convert(@"
class C {
    void Test(object o) {
        switch (o) {
            case string s:
                System.Console.WriteLine(s);
                break;
            case int i:
                System.Console.WriteLine(i);
                break;
            default:
                System.Console.WriteLine(""other"");
                break;
        }
    }
}");
        Assert.True(result.Success);
        // Switch with pattern cases should become if-else chain
        Assert.Contains("if (", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("instanceof String s", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("instanceof int i", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("else", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain switch keyword (converted to if-else)
        Assert.DoesNotContain("switch (o)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchStmt_PatternCase_WhenClause()
    {
        var result = Convert(@"
class C {
    void Test(object o) {
        switch (o) {
            case int i when i > 0:
                System.Console.WriteLine(""positive"");
                break;
            case int i when i < 0:
                System.Console.WriteLine(""negative"");
                break;
            default:
                System.Console.WriteLine(""zero or non-int"");
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("instanceof int i", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("if (", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("i > 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchStmt_PatternCase_ConstantNull()
    {
        var result = Convert(@"
class C {
    void Test(object o) {
        switch (o) {
            case null:
                System.Console.WriteLine(""null"");
                break;
            case string s:
                System.Console.WriteLine(s);
                break;
        }
    }
}");
        Assert.True(result.Success);
        // null check via Objects.equals or == null
        Assert.Contains("null", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("instanceof String s", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════
    //  RecursivePattern (property pattern)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SwitchExpr_RecursivePropertyPattern()
    {
        var result = Convert(@"
class Point { public int X { get; set; } public int Y { get; set; } }
class C {
    string Test(Point p) => p switch {
        { X: 0, Y: 0 } => ""origin"",
        { X: 0 } => ""y-axis"",
        _ => ""other""
    };
}");
        Assert.True(result.Success);
        Assert.Contains("getX()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getY()", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
