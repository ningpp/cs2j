using CSharpToJava.Core.Transformers;
using Xunit;

namespace CSharpToJava.Tests;

public class HolderTypeResolverTests
{
    [Theory]
    [InlineData("int", "io.github.ningpp.compat.IntHolder")]
    [InlineData("long", "io.github.ningpp.compat.LongHolder")]
    [InlineData("double", "io.github.ningpp.compat.DoubleHolder")]
    [InlineData("float", "io.github.ningpp.compat.FloatHolder")]
    [InlineData("boolean", "io.github.ningpp.compat.BoolHolder")]
    [InlineData("char", "io.github.ningpp.compat.CharHolder")]
    [InlineData("short", "io.github.ningpp.compat.ShortHolder")]
    [InlineData("byte", "io.github.ningpp.compat.ByteHolder")]
    public void GetHolderType_PrimitiveTypes_ReturnsDedicatedHolder(string javaType, string expected)
    {
        Assert.Equal(expected, HolderTypeResolver.GetHolderType(javaType));
    }

    [Theory]
    [InlineData("String", "io.github.ningpp.compat.ObjectHolder<String>")]
    [InlineData("List<Integer>", "io.github.ningpp.compat.ObjectHolder<List<Integer>>")]
    [InlineData("MyCustomClass", "io.github.ningpp.compat.ObjectHolder<MyCustomClass>")]
    public void GetHolderType_ReferenceTypes_ReturnsObjectHolder(string javaType, string expected)
    {
        Assert.Equal(expected, HolderTypeResolver.GetHolderType(javaType));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void GetHolderType_EmptyOrNull_ReturnsFallback(string? javaType)
    {
        Assert.Equal("io.github.ningpp.compat.ObjectHolder<Object>", HolderTypeResolver.GetHolderType(javaType!));
    }

    [Fact]
    public void GetHolderInstantiation_PrimitiveHolder_ReturnsNew()
    {
        Assert.Equal("new io.github.ningpp.compat.IntHolder()", HolderTypeResolver.GetHolderInstantiation("io.github.ningpp.compat.IntHolder"));
    }

    [Fact]
    public void GetHolderInstantiation_ObjectHolder_UsesDiamond()
    {
        Assert.Equal("new io.github.ningpp.compat.ObjectHolder<>()", HolderTypeResolver.GetHolderInstantiation("io.github.ningpp.compat.ObjectHolder<String>"));
    }

    [Fact]
    public void GetHolderInstantiationWithValue_PrimitiveHolder_PassesValue()
    {
        Assert.Equal("new io.github.ningpp.compat.IntHolder(x)", HolderTypeResolver.GetHolderInstantiationWithValue("io.github.ningpp.compat.IntHolder", "x"));
    }

    [Fact]
    public void GetHolderInstantiationWithValue_ObjectHolder_PassesValueWithDiamond()
    {
        Assert.Equal("new io.github.ningpp.compat.ObjectHolder<>(x)", HolderTypeResolver.GetHolderInstantiationWithValue("io.github.ningpp.compat.ObjectHolder<String>", "x"));
    }

    [Theory]
    [InlineData("io.github.ningpp.compat.IntHolder", true)]
    [InlineData("io.github.ningpp.compat.LongHolder", true)]
    [InlineData("io.github.ningpp.compat.ObjectHolder<String>", true)]
    [InlineData("io.github.ningpp.compat.ObjectHolder<List<Integer>>", true)]
    [InlineData("IntHolder", true)]
    [InlineData("ObjectHolder<String>", true)]
    [InlineData("String", false)]
    [InlineData("int", false)]
    [InlineData("List<IntHolder>", false)]
    public void IsHolderType_IdentifiesCorrectly(string typeName, bool expected)
    {
        Assert.Equal(expected, HolderTypeResolver.IsHolderType(typeName));
    }
}
