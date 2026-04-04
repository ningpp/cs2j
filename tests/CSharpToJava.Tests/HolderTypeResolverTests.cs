using CSharpToJava.Core.Transformers;
using Xunit;

namespace CSharpToJava.Tests;

public class HolderTypeResolverTests
{
    [Theory]
    [InlineData("int", "IntHolder")]
    [InlineData("long", "LongHolder")]
    [InlineData("double", "DoubleHolder")]
    [InlineData("float", "FloatHolder")]
    [InlineData("boolean", "BoolHolder")]
    [InlineData("char", "CharHolder")]
    [InlineData("short", "ShortHolder")]
    [InlineData("byte", "ByteHolder")]
    public void GetHolderType_PrimitiveTypes_ReturnsDedicatedHolder(string javaType, string expected)
    {
        Assert.Equal(expected, HolderTypeResolver.GetHolderType(javaType));
    }

    [Theory]
    [InlineData("String", "ObjectHolder<String>")]
    [InlineData("List<Integer>", "ObjectHolder<List<Integer>>")]
    [InlineData("MyCustomClass", "ObjectHolder<MyCustomClass>")]
    public void GetHolderType_ReferenceTypes_ReturnsObjectHolder(string javaType, string expected)
    {
        Assert.Equal(expected, HolderTypeResolver.GetHolderType(javaType));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void GetHolderType_EmptyOrNull_ReturnsFallback(string? javaType)
    {
        Assert.Equal("ObjectHolder<Object>", HolderTypeResolver.GetHolderType(javaType!));
    }

    [Fact]
    public void GetHolderInstantiation_PrimitiveHolder_ReturnsNew()
    {
        Assert.Equal("new IntHolder()", HolderTypeResolver.GetHolderInstantiation("IntHolder"));
    }

    [Fact]
    public void GetHolderInstantiation_ObjectHolder_UsesDiamond()
    {
        Assert.Equal("new ObjectHolder<>()", HolderTypeResolver.GetHolderInstantiation("ObjectHolder<String>"));
    }

    [Fact]
    public void GetHolderInstantiationWithValue_PrimitiveHolder_PassesValue()
    {
        Assert.Equal("new IntHolder(x)", HolderTypeResolver.GetHolderInstantiationWithValue("IntHolder", "x"));
    }

    [Fact]
    public void GetHolderInstantiationWithValue_ObjectHolder_PassesValueWithDiamond()
    {
        Assert.Equal("new ObjectHolder<>(x)", HolderTypeResolver.GetHolderInstantiationWithValue("ObjectHolder<String>", "x"));
    }

    [Theory]
    [InlineData("IntHolder", true)]
    [InlineData("LongHolder", true)]
    [InlineData("ObjectHolder<String>", true)]
    [InlineData("ObjectHolder<List<Integer>>", true)]
    [InlineData("String", false)]
    [InlineData("int", false)]
    [InlineData("List<IntHolder>", false)]
    public void IsHolderType_IdentifiesCorrectly(string typeName, bool expected)
    {
        Assert.Equal(expected, HolderTypeResolver.IsHolderType(typeName));
    }
}
