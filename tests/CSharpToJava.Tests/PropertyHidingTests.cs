using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# property hiding (using `new` keyword or implicit hiding)
/// does not produce a Java override that causes incorrect virtual dispatch.
/// In C#, hiding is non-virtual dispatch; in Java, all methods are virtual.
/// The converter must skip generating getters/setters for hiding properties
/// so that base-class dispatch is preserved.
/// </summary>
public class PropertyHidingTests
{
    [Fact]
    public void PropertyHiding_WithNewKeyword_DoesNotGenerateOverrideGetter()
    {
        var result = Convert(@"
class Base {
    private string _scheme = """";
    public string SchemeName { get { return _scheme; } }
}
class Derived : Base {
    private string scheme_name;
    public new string SchemeName { get { return scheme_name; } }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The Derived class should NOT have a getSchemeName() that overrides Base.getSchemeName()
        // because in C#, hiding is non-virtual — base-class references must call the base getter.
        var derivedCode = ExtractClassCode(result.GeneratedCode, "Derived");
        Assert.DoesNotContain("getSchemeName", derivedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyHiding_WithoutNewKeyword_DoesNotGenerateOverrideGetter()
    {
        var result = Convert(@"
class Base {
    private string _scheme = """";
    public string SchemeName { get { return _scheme; } }
}
class Derived : Base {
    private string scheme_name;
    public string SchemeName { get { return scheme_name; } }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Even without `new` keyword, the property hides the base class property.
        // The converter should detect this via semantic model and skip the getter.
        var derivedCode = ExtractClassCode(result.GeneratedCode, "Derived");
        Assert.DoesNotContain("getSchemeName", derivedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyHiding_NonHiddenProperty_StillGenerated()
    {
        var result = Convert(@"
class Base {
    public string Name { get { return ""base""; } }
}
class Derived : Base {
    public string Extra { get { return ""extra""; } }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Non-hidden properties should still be generated
        Assert.Contains("getExtra", result.GeneratedCode, StringComparison.Ordinal);
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

    private static string ExtractClassCode(string fullCode, string className)
    {
        // Find the class declaration and extract its body
        var classKeyword = $"class {className}";
        var startIdx = fullCode.IndexOf(classKeyword, StringComparison.Ordinal);
        if (startIdx < 0) return "";
        // Find the opening brace
        var braceStart = fullCode.IndexOf('{', startIdx);
        if (braceStart < 0) return "";
        // Find the matching closing brace
        var depth = 0;
        var endIdx = braceStart;
        for (int i = braceStart; i < fullCode.Length; i++)
        {
            if (fullCode[i] == '{') depth++;
            else if (fullCode[i] == '}')
            {
                depth--;
                if (depth == 0) { endIdx = i; break; }
            }
        }
        return fullCode.Substring(braceStart, endIdx - braceStart + 1);
    }
}
