using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# properties that hide base members with the `new` keyword.
/// </summary>
public class PropertyHidingTests : ConversionTestBase
{
    [Fact]
    public void ExplicitHidingProperty_WithDerivedReturnType_EmitsGetter()
    {
        var result = Convert(@"
class BaseAttr
{
    public virtual string Name { get; set; }
}

class DerivedAttr : BaseAttr
{
    public string Extra { get; set; }
}

class Base
{
    public virtual BaseAttr Attribute { get; set; }
}

class Derived : Base
{
    public new virtual DerivedAttr Attribute
    {
        get { return (DerivedAttr)base.Attribute; }
        set { base.Attribute = value; }
    }

    public string GetExtra() { return Attribute.Extra; }
}");
        AssertConversion(result, "return getAttribute().getExtra();");
    }
}
