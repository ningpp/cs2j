using CSharpToJava.Core.JavaKeywords;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// JK-0 按符号种类判定：字段/属性的名字是序列化与反射契约，不因 decap 命中而改名；
/// 方法/局部函数会首字母小写，故 decap 命中的名字仍必须改名。
/// </summary>
public class JavaKeywordSymbolKindTests
{
    private static (string OutputCode, JavaKeywordStatistics Stats) Rewrite(string sourceCode)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions, path: "Sample.cs");
        var compilation = CSharpCompilation.Create(
            "jk-symbol-kind-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);

        var preprocessor = new JavaReservedWordPreprocessor();
        var statistics = new JavaKeywordStatistics();
        var result = preprocessor.RewriteFile(
            sourceCode, model, statistics, new List<JavaKeywordDiagnostic>());
        return (result.OutputCode, statistics);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    [Fact]
    public void Field_Names_True_Float_Int_Class_AreNotRenamed()
    {
        var sourceCode =
            "public class C { public bool True; public float Float; public int Int; public int Class; }";
        var (output, stats) = Rewrite(sourceCode);
        Assert.Equal(sourceCode, output);
        Assert.Equal(0, stats.SymbolsRenamed);
    }

    [Fact]
    public void Property_Names_True_Float_Int_Class_AreNotRenamed()
    {
        var sourceCode =
            "public class C { public bool True { get; set; } public float Float { get; set; } " +
            "public int Int { get; set; } public int Class { get; set; } }";
        var (output, stats) = Rewrite(sourceCode);
        Assert.Equal(sourceCode, output);
        Assert.Equal(0, stats.SymbolsRenamed);
    }

    [Fact]
    public void Method_Names_Float_And_Class_AreRenamedToValueSuffix()
    {
        var sourceCode =
            "public class C { public void Float() { } public void Class() { } " +
            "public void M() { Float(); Class(); } }";
        var (output, stats) = Rewrite(sourceCode);
        Assert.Contains("void FloatValue()", output, StringComparison.Ordinal);
        Assert.Contains("void ClassValue()", output, StringComparison.Ordinal);
        Assert.Contains("FloatValue();", output, StringComparison.Ordinal);
        Assert.Contains("ClassValue();", output, StringComparison.Ordinal);
        Assert.Equal(2, stats.SymbolsRenamed);
    }

    [Fact]
    public void LocalFunction_Float_IsRenamedToFloatValue()
    {
        var sourceCode = "public class C { public void M() { void Float() { } Float(); } }";
        var (output, _) = Rewrite(sourceCode);
        Assert.Contains("void FloatValue()", output, StringComparison.Ordinal);
        Assert.Contains("FloatValue();", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Verbatim_Class_Field_IsStillRenamed()
    {
        var sourceCode = "public class C { public int @class; public int M() => @class; }";
        var (output, stats) = Rewrite(sourceCode);
        Assert.Contains("int classValue;", output, StringComparison.Ordinal);
        Assert.Contains("=> classValue", output, StringComparison.Ordinal);
        Assert.DoesNotContain("@class", output, StringComparison.Ordinal);
        Assert.Equal(1, stats.SymbolsRenamed);
    }
}
