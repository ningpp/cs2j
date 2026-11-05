using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that pre-statements produced inside an expression-bodied property getter
/// (e.g. lazy initialization via ??=) are absorbed by the getter itself and do not
/// leak into the following member. Reproduces Match.Groups (??= inside an
/// expression-bodied getter) leaking `if (_groupcoll == null) ...` into
/// MatchCollection's _regex field initializer (javac: illegal start of expression).
/// </summary>
public class GetterPreStatementLeakTests
{
    /// <summary>
    /// C# 小写属性名（真实链 dotnet.xml.QName.prefix）的无接收者读取必须与声明侧
    /// 同一命名规则（getPrefix()）；直接用 get+原名会发 getprefix() 找不到符号。
    /// </summary>
    [Fact]
    public void LowercaseProperty_UnqualifiedRead_UsesCapitalizedBeanGetter()
    {
        var result = Convert(@"
class QName
{
    public string prefix { get; }

    public override string ToString()
    {
        return prefix.Length == 0 ? """" : prefix;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("getPrefix()", code);
        Assert.DoesNotContain("getprefix()", code);
    }

    [Fact]
    public void CoalesceAssignmentInExpressionGetter_DoesNotLeakIntoNextField()
    {
        var result = Convert(@"
class Holder { }

class Owner
{
    private Holder _holder;

    public Holder Items => _holder ??= new Holder();
}

class Follower
{
    private readonly int _value;
    private readonly string _name;

    internal Follower(int value, string name)
    {
        _value = value;
        _name = name;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The lazy init must live inside the getter...
        Assert.Contains("if (_holder == null) _holder = new Holder()", code);
        // ...and must not leak into members of the following class (cross-class
        // contamination observed as MatchCollection._regex receiving Match's lazy init).
        var followerIdx = code.IndexOf("class Follower", System.StringComparison.Ordinal);
        Assert.True(followerIdx >= 0, "Follower class missing\n" + code);
        var followerPart = code.Substring(followerIdx);
        Assert.DoesNotContain("if (_holder == null)", followerPart);
        Assert.DoesNotContain("Object initializer", followerPart);
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
