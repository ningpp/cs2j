using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# type.FullName access correctly maps to TypeHelper.getFullName(type)
/// on java.lang.Class objects. This verifies the fix for the missing getFullName()
/// method error in generated Java code.
/// See: ExpressionTransformerFacade.cs (line 253) and
///      IdentifierExpressionTransformer.TryResolvePropertyByType.
/// </summary>
public class TypeFullNameTests
{
    /// <summary>
    /// C# Type.FullName → TypeHelper.getFullName(type) for parameter-typed variables.
    /// The previous (buggy) behavior generated type.getFullName() which does not
    /// exist on java.lang.Class.
    /// </summary>
    [Fact]
    public void TypeFullName_OnParameterVariable_MapsToTypeHelper()
    {
        var result = Convert(@"
using System;
class Test {
    string Describe(Type type) {
        return type.FullName;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("TypeHelper.getFullName(type)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".getFullName()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// C# typeof(T).FullName → TypeHelper.getFullName(Type.class) for compile-time types.
    /// Both parameter and typeof() forms must route through TypeHelper.
    /// </summary>
    [Fact]
    public void TypeFullName_OnTypeofExpression_MapsToTypeHelper()
    {
        var result = Convert(@"
using System;
class Test {
    string Describe() {
        return typeof(int).FullName;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("TypeHelper.getFullName(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".getFullName()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensure the Java import for TypeHelper is generated.
    /// </summary>
    [Fact]
    public void TypeFullName_GeneratesTypeHelperImport()
    {
        var result = Convert(@"
using System;
class Test {
    string Describe(Type type) {
        return ""type: "" + type.FullName;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.TypeHelper;", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Type.FullName used inside a method argument or string interpolation,
    /// mirroring the XmlSerializationWriter writeArray pattern that originally
    /// surfaced the bug.
    /// </summary>
    [Fact]
    public void TypeFullName_InStringConcatenation_MapsToTypeHelper()
    {
        var result = Convert(@"
using System;
class Test {
    void Validate(Type type) {
        if (type != null) {
            throw new InvalidOperationException(""not array like type "" + type.FullName);
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("TypeHelper.getFullName(type)", result.GeneratedCode, StringComparison.Ordinal);
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
