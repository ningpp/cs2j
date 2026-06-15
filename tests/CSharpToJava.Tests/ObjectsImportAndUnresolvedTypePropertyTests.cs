using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for bug fixes related to:
/// 1. Objects.equals/hash/hashCode missing import java.util.Objects
/// 2. Unresolved type property access falling back to raw field access instead of getter
/// 3. System.Xml.XmlReader type mapping
/// </summary>
public class ObjectsImportAndUnresolvedTypePropertyTests
{
    // ── Fix 1: Objects.equals missing import in string equality ─────────────

    [Fact]
    public void StringEquality_GeneratesObjectsEqualsImport()
    {
        var result = Convert(@"
class Demo {
    void M(string a, string b) {
        if (a == b) { }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Objects.equals(a, b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Objects;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringInequality_GeneratesObjectsEqualsImport()
    {
        var result = Convert(@"
class Demo {
    void M(string a, string b) {
        if (a != b) { }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("!Objects.equals(a, b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Objects;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringNullComparison_DoesNotUseObjectsEquals()
    {
        var result = Convert(@"
class Demo {
    void M(string a) {
        if (a == null) { }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("Objects.equals", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 2: Unresolved type property access generates getter ─────────────

    [Fact]
    public void UnresolvedType_PropertyAccess_GeneratesGetter()
    {
        // XmlReader is unresolved (System.Xml not referenced), but its property
        // Name should still be converted to getName() instead of raw .Name
        var result = Convert(@"
using System.Xml;
namespace Test {
    public class Demo {
        XmlReader XmlReader { get; set; }
        public void GetElementTag() {
            if (XmlReader.Name == ""graph"")
                System.Console.WriteLine(""test"");
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("getXmlReader().getName()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getXmlReader().Name", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedType_PropertyAccess_WithObjectsEqualsImport()
    {
        // Both fixes should work together: getter pattern + Objects.equals import
        var result = Convert(@"
using System.Xml;
namespace Test {
    public class Demo {
        XmlReader Reader { get; set; }
        public void Check() {
            if (Reader.Name == ""test"") { }
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("getReader().getName()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Objects;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedType_MultipleProperties_GenerateGetters()
    {
        var result = Convert(@"
namespace Test {
    public class Demo {
        UnknownType Item { get; set; }
        public void M() {
            var a = Item.Value;
            var b = Item.Name;
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("getItem().getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getItem().getName()", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 3: System.Xml.XmlReader type mapping ────────────────────────────

    [Fact]
    public void SystemXml_XmlReader_MappedToDotnetXml()
    {
        var result = Convert(@"
using System.Xml;
namespace Test {
    public class Demo {
        XmlReader Reader { get; set; }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import dotnet.xml", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemXml_ReadState_EnumMember_IsQualifiedToAvoidCompatAmbiguity()
    {
        var result = Convert(@"
using System.Xml;
namespace Test {
    public class Demo {
        XmlReader Reader { get; set; }
        public bool IsDone() {
            return Reader.ReadState == ReadState.EndOfFile;
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("import dotnet.xml.ReadState;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getReader().getReadState() == ReadState.EndOfFile", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 4: Record equals/hashCode Objects import ────────────────────────

    [Fact]
    public void Record_ImmutableClass_Equals_ContainsObjectsImport()
    {
        var result = ConvertNoRecords(@"
public record Point(int X, int Y);
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Objects.equals", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Objects;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_ImmutableClass_HashCode_ContainsObjectsImport()
    {
        var result = ConvertNoRecords(@"
public record Point(int X, int Y);
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Objects.hash", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Objects;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_ImmutableClass_NoFields_ContainsObjectsImport()
    {
        var result = ConvertNoRecords(@"
public record Empty();
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Objects.hash()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Objects;", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── End-to-end: original user scenario ──────────────────────────────────

    [Fact]
    public void XmlReaderDemo_ConvertsCorrectly()
    {
        var result = Convert(@"
using System.Xml;
namespace Microsoft.Msagl.DebugHelpers.Persistence
{
    public class Demo {
        XmlReader XmlReader { get; set; }
        public void GetElementTag()
        {
            if (XmlReader.Name == ""graph"")
                System.Console.WriteLine(""<GRAPH>"");
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Property access should use getter pattern
        Assert.Contains("getXmlReader().getName()", result.GeneratedCode, StringComparison.Ordinal);
        // String equality should use Objects.equals
        Assert.Contains("Objects.equals(getXmlReader().getName(), \"graph\")", result.GeneratedCode, StringComparison.Ordinal);
        // Objects import should be present
        Assert.Contains("import java.util.Objects;", result.GeneratedCode, StringComparison.Ordinal);
        // XmlReader import should be present
        Assert.Contains("import dotnet.xml", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    private static ConversionResult ConvertNoRecords(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions { UseRecords = false },
        });
    }
}
