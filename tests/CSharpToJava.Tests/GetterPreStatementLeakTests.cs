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
