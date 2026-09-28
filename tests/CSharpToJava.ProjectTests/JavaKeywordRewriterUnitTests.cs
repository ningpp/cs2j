using System.IO;
using CSharpToJava.Core.JavaKeywords;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

public class JavaKeywordRewriterUnitTests
{
    private readonly ITestOutputHelper _output;
    public JavaKeywordRewriterUnitTests(ITestOutputHelper output) => _output = output;

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview);

    internal static SemanticModel ParseWithSemantics(string src,
        LanguageVersion? version = null, params string[] extra)
    {
        var options = version is { } v ? new CSharpParseOptions(v) : ParseOptions;
        var tree = CSharpSyntaxTree.ParseText(src, options);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, options)).ToArray();
        var compilation = CSharpCompilation.Create(
            "jk-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    internal static (string OutputCode, JavaKeywordStatistics Stats,
        List<JavaKeywordDiagnostic> Diagnostics) Rewrite(string src,
        JavaKeywordOptions? options = null, LanguageVersion? version = null,
        params string[] extra)
    {
        var model = ParseWithSemantics(src, version, extra);
        var pp = new JavaReservedWordPreprocessor(options ?? new JavaKeywordOptions());
        var stats = new JavaKeywordStatistics();
        var diags = new List<JavaKeywordDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, stats, diags);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    internal static void AssertOutputCompiles(string output, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create("jk-verify",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    [Fact]
    public void Models_DefaultCounters_AreZero()
    {
        var stats = new JavaKeywordStatistics();
        Assert.Equal(0, stats.SymbolsRenamed);
        Assert.Equal(0, stats.PlansRolledBack);
        var d = new JavaKeywordDiagnostic(
            JavaKeywordSeverity.Warning, JavaKeywordDiagnosticCode.JK020,
            "operator plan skipped", "Lib/Foo.cs", 7, 3, "Lib.Foo");
        Assert.Equal("JK020", d.Code.ToString());
        Assert.Equal("Lib/Foo.cs(7,3): warning JK020: [Lib.Foo] operator plan skipped", d.ToMsBuildString());
    }

    [Fact]
    public void NeedsEscape_True_WhenNameOrDecapIsJavaKeyword()
    {
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("True"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("assert"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("@class"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("Null"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("_"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("yield"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("@true"));
    }

    [Fact]
    public void NeedsEscape_False_WhenNeitherNameNorDecapIsKeyword()
    {
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("foo"));
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("FALSE"));
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("WHILE"));
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("@Foo"));
    }

    [Fact]
    public void NewName_AppendsValue_ToStrippedName()
    {
        Assert.Equal("TrueValue", JavaKeywordSyntaxFacts.NewName("True"));
        Assert.Equal("assertValue", JavaKeywordSyntaxFacts.NewName("assert"));
        Assert.Equal("classValue", JavaKeywordSyntaxFacts.NewName("@class"));
        Assert.Equal("_Value", JavaKeywordSyntaxFacts.NewName("_"));
        Assert.Equal("yieldValue", JavaKeywordSyntaxFacts.NewName("yield"));
    }

    [Fact]
    public void Rename_Method_True_To_TrueValue()
    {
        var src = "public class C { public void True() { } public void M() => True(); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("TrueValue()", output);
        Assert.DoesNotContain("void True()", output);
        Assert.Contains("void TrueValue()", output);
        Assert.Equal(1, stats.SymbolsRenamed);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void NoRename_WhenNameIsNotKeyword()
    {
        var src = "public class C { public void Foo() { } }";
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(0, stats.SymbolsRenamed);
    }

    [Fact]
    public void NoRename_AllUppercase_FALSE()
    {
        // FALSE → decap = fALSE, not a keyword → no rename
        var src = "public class C { public int FALSE = 42; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(0, stats.SymbolsRenamed);
    }

    [Fact]
    public void Rename_Property_Null_To_NullValue()
    {
        var src = "public class C { public int Null { get; set; } public void M() => _ = Null; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("int NullValue { get; set; }", output);
        Assert.Contains("_ = NullValue", output);
        Assert.Equal(1, stats.SymbolsRenamed);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Field_Assert_To_AssertValue()
    {
        var src = "public class C { public int assert = 42; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("int assertValue = 42", output);
        Assert.Equal(1, stats.SymbolsRenamed);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_PascalCase_Assert_To_AssertValue()
    {
        var src = "public class C { public void Assert() { } public void M() => Assert(); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("void AssertValue()", output);
        Assert.Contains("AssertValue()", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Verbatim_Class_To_ClassValue()
    {
        var src = "public class C { public event System.Action? @class; public void M() => @class?.Invoke(); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("event System.Action? classValue", output);
        Assert.Contains("classValue?.Invoke()", output);
        Assert.DoesNotContain("@class", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Local_Yield_To_YieldValue()
    {
        var src = "public class C { public void M() { int yield = 1; _ = yield; } }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("int yieldValue = 1", output);
        Assert.Contains("_ = yieldValue", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Parameter_True_To_TrueValue()
    {
        var src = "public class C { public void M(int True) => _ = True; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("int TrueValue", output);
        Assert.Contains("_ = TrueValue", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Type_Null_To_NullValue()
    {
        var src = "public class Null { } public class C { public Null? M() => null; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("class NullValue", output);
        Assert.Contains("NullValue? M()", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Collision_TrueValue_Existing_To_TrueValue2()
    {
        var src = "public class C { public void True() { } public void TrueValue() { } public void M() => True(); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("void TrueValue2()", output);
        Assert.Contains("TrueValue2()", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Nameof_BoundToRenamedSymbol_ReplacedWithStringLiteral()
    {
        var src = "public class C { public void True() { } public string M() => nameof(True); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("\"True\"", output);
        Assert.DoesNotContain("nameof(True)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Nameof_NotBoundToRenamedSymbol_Unchanged()
    {
        var src = "public class C { public void Foo() { } public string M() => nameof(Foo); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("nameof(Foo)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void CrossFile_Reference_Renamed()
    {
        var lib = "public class Lib { public void True() { } }";
        var app = "public class App { public void M(Lib lib) => lib.True(); }";
        var model = ParseWithSemantics(app, extra: lib);
        var pp = new JavaReservedWordPreprocessor();
        var stats = new JavaKeywordStatistics();
        var diags = new List<JavaKeywordDiagnostic>();
        var result = pp.RewriteFile(app, model, stats, diags);
        Assert.Contains("TrueValue()", result.OutputCode);
        // 验证编译时使用改写后的 lib 源码
        var rewrittenLib = "public class Lib { public void TrueValue() { } }";
        AssertOutputCompiles(result.OutputCode, rewrittenLib);
    }

    // ---- JK-5~JK-8: operator true/false 降级 ----

    [Fact]
    public void OperatorTrueFalse_DeclarationDowngraded_ToIsTrueIsFalse()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
}";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public static bool IsTrue(Foo x)", output);
        Assert.Contains("public static bool IsFalse(Foo x)", output);
        Assert.DoesNotContain("operator true", output);
        Assert.DoesNotContain("operator false", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IfStatement_WithOpTrue_WrappedWithIsTrue()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M(Foo f) { if (f) { } }
}";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("if (Foo.IsTrue(f))", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void WhileStatement_WithOpTrue_WrappedWithIsTrue()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M(Foo f) { while (f) { } }
}";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("while (Foo.IsTrue(f))", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ConditionalExpression_WithOpTrue_WrappedWithIsTrue()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M(Foo f) { var x = f ? 1 : 2; }
}";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("Foo.IsTrue(f) ? 1 : 2", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void OperatorPlan_InfeasibleExpressionTree_JK020()
    {
        // 表达式树 lambda 内的布尔站点（if 条件）触发 JK-8
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M()
    {
        System.Linq.Expressions.Expression<System.Func<Foo, bool>> e = f =>
        {
            if (f) return true;
            return false;
        };
    }
}";
        var (output, stats, diags) = Rewrite(src);
        Assert.Contains("operator true", output);
        Assert.Contains("operator false", output);
        Assert.Contains(diags ?? new(), d => d.Code == JavaKeywordDiagnosticCode.JK020);
    }

    [Fact]
    public void OperatorPlan_NameCollision_JK020()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public static bool IsTrue(Foo x) => x.V > 0;
}";
        var (output, stats, diags) = Rewrite(src);
        Assert.Contains("operator true", output);
        Assert.Contains(diags ?? new(), d => d.Code == JavaKeywordDiagnosticCode.JK020);
    }

    [Fact]
    public void SkipConfig_ExcludesOperatorPlan_JK010()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M(Foo f) { if (f) { } }
}";
        var options = new JavaKeywordOptions
        {
            SkipQualifiedNames = new HashSet<string> { "Foo" },
        };
        var (output, stats, diags) = Rewrite(src, options);
        Assert.Contains("operator true", output);
        Assert.Contains(diags ?? new(), d => d.Code == JavaKeywordDiagnosticCode.JK010);
    }
}
