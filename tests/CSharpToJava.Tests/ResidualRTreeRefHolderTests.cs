using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;

namespace CSharpToJava.Tests;

/// <summary>
/// The ref/out argument path already emits an <c>ObjectHolder&lt;T&gt;</c> at the
/// declaration and passes that variable. A former residual RTree rule wrapped
/// the argument a second time, producing
/// <c>ObjectHolder&lt;ObjectHolder&lt;Rectangle&gt;&gt;</c>, which javac cannot
/// infer.
/// </summary>
public class ResidualRTreeRefHolderTests
{
    [Fact]
    public void RTreeQueryCall_PassesGeneratedHolderWithoutSecondWrap()
    {
        var rewriter = new ResidualExtensionCallRewriter();
        var statement = new JavaRawStatement(
            "io.github.ningpp.compat.ObjectHolder<Rectangle> _queryRef = "
            + "new io.github.ningpp.compat.ObjectHolder<>(query);\n"
            + "var list = rTreeQuery_Foo(points, _queryRef, points);\n"
            + "query = _queryRef.value;");

        var result = rewriter.VisitRawStatement(statement).Code;

        Assert.DoesNotContain(
            "new io.github.ningpp.compat.ObjectHolder<>(_queryRef)",
            result,
            StringComparison.Ordinal);
        Assert.Contains(
            "rTreeQuery_Foo(points, _queryRef, points);",
            result,
            StringComparison.Ordinal);
    }
}
