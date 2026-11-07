using CSharpToJava.TypeMapping;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// 方法名映射的「接收者简名」兜底：转换编译只引用框架程序集（xunit 等测试框架
/// 不在引用集里），<c>Assert.Throws&lt;T&gt;</c> 的方法符号可能缺失；调用点仍必须
/// 落到 compat 的 <c>throws_</c>，而不是 Pascal→camel 后的 Java 关键字转义
/// <c>throwsValue</c>。仅在候选映射唯一时命中，避免同简名的 MSTest/NUnit 类型混淆。
/// </summary>
public class TypeMappingReceiverFallbackTests
{
    private static TypeMappingRegistry CreateRegistry() => new("config/TypeMappings.json");

    [Fact]
    public void UniqueReceiverSimpleNameMapping_IsReturned()
    {
        var registry = CreateRegistry();
        Assert.Equal("throws_", registry.MapMethodByReceiverSimpleName("Assert", "Throws"));
    }

    [Fact]
    public void SourceTypeRedirect_IsLoadedFromConfig()
    {
        var registry = CreateRegistry();

        // Java 无 IL 生成：Reflection.Emit 版成员访问器必须重定向到反射回退实现。
        Assert.Equal(
            "ReflectionMemberAccessor",
            registry.MapSourceTypeRedirect("System.Text.Json.Serialization.ReflectionEmitMemberAccessor"));
        Assert.Null(registry.MapSourceTypeRedirect("Missing.Type"));
    }

    [Fact]
    public void UnknownReceiverSimpleName_ReturnsNull()
    {
        var registry = CreateRegistry();
        Assert.Null(registry.MapMethodByReceiverSimpleName("NotACompatType", "Throws"));
    }

    [Fact]
    public void NonMappedMethodName_ReturnsNull()
    {
        var registry = CreateRegistry();
        Assert.Null(registry.MapMethodByReceiverSimpleName("Assert", "ThrowsValue"));
    }
}
