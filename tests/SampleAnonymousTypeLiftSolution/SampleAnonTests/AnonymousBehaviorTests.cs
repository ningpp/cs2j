using Xunit;

namespace SampleAnonTests;

/// <summary>
/// 提升后类的运行时行为等价闸门（设计 §7.2）：这些用例自身也含匿名创建点，
/// 会被 anonymous-type-lift 改写；verifyTests 在中间树上 dotnet test 时断言的
/// 就是生成的 internal sealed 类的行为。
/// </summary>
public class AnonymousBehaviorTests
{
    [Fact]
    public void Equals_matches_by_value_and_rejects_null_and_other_shapes()
    {
        var a = new { X = 1, Y = "s" };
        var same = new { X = 1, Y = "s" };
        var differentValue = new { X = 1, Y = "t" };

        Assert.True(a.Equals(same));
        Assert.False(a.Equals(differentValue));
        Assert.False(a.Equals(null));
        // 异形（属性序列不同）：is 精确运行时类型判定，永不相等。
        Assert.False(a.Equals(new { X = 1 }));
    }

    [Fact]
    public void Equality_operators_stay_reference_equality_D1()
    {
        // D1 核心回归：生成类不声明 ==/!= 运算符，运算解析为引用相等。
        var a = new { X = 1, Y = "s" };
        var b = new { X = 1, Y = "s" };

        Assert.False(a == b);
        Assert.True(a != b);
        var sameReference = a;
        Assert.True(a == sameReference);
    }

    [Fact]
    public void GetHashCode_equal_values_share_hash_and_dictionary_keys_dedupe()
    {
        var a = new { X = 1, Y = "s" };
        var b = new { X = 1, Y = "s" };

        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // 同值两实例作 GroupBy / Dictionary 键只计一组。
        var groups = new[] { a, b }.GroupBy(x => x).ToArray();
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count());

        var counts = new[] { a, b }
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Single(counts);
        Assert.Equal(2, counts[a]);
    }

    [Fact]
    public void ToString_matches_anonymous_format_with_null_and_nesting()
    {
        var a = new { X = 1, Y = "s" };
        Assert.Equal("{ X = 1, Y = s }", a.ToString());

        // null 经 String.Concat(params object[]) 渲染为空串。
        Assert.Equal("{ S =  }", new { S = (string?)null }.ToString());

        // 嵌套形状递归命中内层 ToString（装箱虚调用）。
        Assert.Equal(
            "{ Outer = { Inner = 7 } }",
            new { Outer = new { Inner = 7 } }.ToString());
    }
}
