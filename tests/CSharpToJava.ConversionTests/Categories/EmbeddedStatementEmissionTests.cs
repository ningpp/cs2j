using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Two emission shapes that produced syntactically invalid Java:
///   - a brace-less body on lock/using was interpolated as a
///     <c>JavaSyntaxNode</c>, whose <c>ToString()</c> is the type name, so
///     <c>synchronized (this) { CSharpToJava...JavaStatementNode }</c> leaked;
///   - a delegate constructed over a method group and invoked immediately
///     rendered the method reference form <c>Owner::method(args)</c>, which is
///     not a Java expression.
/// </summary>
public class EmbeddedStatementEmissionTests : ConversionTestBase
{
    [Fact]
    public void Lock_EmbeddedIfBody_EmitsStatementText()
    {
        var result = Convert(@"
public class C
{
    private int _v;
    public int M(bool b)
    {
        lock (this)
            if (b) { _v = 1; } else { _v = 2; }
        return _v;
    }
}");
        AssertSuccess(result);
        AssertJavaDoesNotContain(result, "JavaStatementNode", "embedded body must render as a statement");
        AssertJavaContains(result, "synchronized (this)", "lock maps to synchronized");
        AssertJavaContains(result, "if (b)", "the embedded if body survives");
    }

    [Fact]
    public void Using_EmbeddedIfBody_EmitsStatementText()
    {
        var result = Convert(@"
using System;
public sealed class R : IDisposable
{
    public void Dispose() { }
}
public class C
{
    private int _v;
    public int M(bool b, R r)
    {
        using (r)
            if (b) { _v = 1; } else { _v = 2; }
        return _v;
    }
}");
        AssertSuccess(result);
        AssertJavaDoesNotContain(result, "JavaStatementNode", "embedded body must render as a statement");
        AssertJavaContains(result, "try (", "using maps to try-with-resources");
        AssertJavaContains(result, "if (b)", "the embedded if body survives");
    }

    [Fact]
    public void DelegateCreationOverMethodGroup_ImmediateInvoke_EmitsDirectCall()
    {
        // The LINQ channel's procedural rewrite emits exactly this shape for
        // Aggregate: new Func<...>(Cs2jLambda_X)(_acc, _item).
        var result = Convert(@"
using System;
public class C
{
    private static string Join(string a, double b) { return a + b; }
    public string M(double d)
    {
        string prefix = ""x"";
        return new Func<string, double, string>(Join)(prefix, d);
    }
}");
        AssertSuccess(result);
        AssertJavaDoesNotContain(result, "::join", "a method reference cannot be called directly");
        AssertJavaContains(result, "join(prefix, d)", "immediate invoke becomes a direct call");
    }
}
