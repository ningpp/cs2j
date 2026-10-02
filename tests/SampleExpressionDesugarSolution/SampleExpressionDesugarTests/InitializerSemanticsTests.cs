using SampleExpressionDesugarLib;
using Xunit;

namespace SampleExpressionDesugarTests;

/// <summary>
/// C24 initializer behavior gate. Every assertion describes native C#
/// object/collection-initializer semantics; the same observations are executed
/// against the C24-preprocessed tree by the project tests, so both runs must
/// agree.
/// </summary>
public static class InitializerSemanticsTests
{
    [Fact]
    public static void ObjectInitializers()
    {
        Assert.Equal("42:0", InitializerDtoScenarios.ConstructArgsPreserved());
        Assert.Equal("1,2,2", InitializerDtoScenarios.MemberOrderAndSideEffects());
        Assert.Equal(0, InitializerDtoScenarios.EmptyInitializer());
    }

    [Fact]
    public static void CollectionInitializers()
    {
        Assert.Equal("8:1,2", CollectionScenarios.CtorArgPreserved());
        Assert.Equal("1-2;3-4", CollectionScenarios.MultiArg());
        Assert.Equal("1,2", CollectionScenarios.Fluent());
        Assert.Equal("1,2,3", CollectionScenarios.Params());
        Assert.Equal("4,5", CollectionScenarios.Extension());
        Assert.Equal("ab:2", CollectionScenarios.DictionaryIndex());
    }

    [Fact]
    public static void NestedAndConditional()
    {
        Assert.Equal("7", NestedScenarios.NestedCreate());
        Assert.Equal("5:1", NestedScenarios.NestedMemberGetterCalledOnce());
        Assert.Equal("x:1", NestedScenarios.ConditionalBranchSideEffects(true));
        Assert.Equal("null:0", NestedScenarios.ConditionalBranchSideEffects(false));
    }

    [Fact]
    public static void TargetTypedNew()
    {
        Assert.Equal("2:12", TargetTypedScenarios.LocalInitializer());
        Assert.Equal("9", TargetTypedScenarios.BareLocal());
        Assert.Equal(new[] { 3, 4 }, TargetTypedScenarios.ReturnWithInitializer());
        Assert.Equal("1", TargetTypedScenarios.NullableTarget());
    }
}
