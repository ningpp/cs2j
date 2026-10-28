using System.IO;
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests.MemberDesugar;

/// <summary>
/// C11 property 去糖回归：MSTest 通过反射按「属性名 TestContext」注入
/// <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestContext</c>。一旦展开成
/// get_TestContext()/set_TestContext() 方法，属性消失、反射注入永远为 null，
/// 所有使用点（TestContext.WriteLine / Holder.TestContext = x）全部 NRE
/// （GraphLayout member-desugar 阶段 186 failed 的根因）。
/// 故这类框架契约属性必须整体保留：声明与使用点都不动。
/// </summary>
public class MemberDesugarTestContextPropertyTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <summary>本编译内的 TestContext 桩：语义判定只看完全限定名，无需引用 MSTest 包。</summary>
    private const string TestFrameworkStub = """
        namespace Microsoft.VisualStudio.TestTools.UnitTesting
        {
            public class TestContext
            {
                public string? TestRunDirectory { get; set; }
                public string? DeploymentDirectory { get; set; }
                public void WriteLine(string format, params object[] args) { }
            }
        }
        """;

    private static (string OutputCode, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diagnostics) Rewrite(string src, params string[] extra)
        => RewriteWithReferences(src, GetReferences(), extra);

    private static (string OutputCode, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diagnostics) RewriteWithReferences(
        string src, IEnumerable<MetadataReference> references, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create(
            "member-desugar-testcontext",
            new[] { tree }.Concat(extraTrees),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            src, compilation.GetSemanticModel(tree), stats, diags);
        return (result.OutputCode, stats, diags);
    }

    /// <summary>与 CLI member-desugar 预处理编译一致：只挂 BCL 引用，不含 MSTest 程序集，
    /// TestContext 因此绑定为 IErrorTypeSymbol。</summary>
    private static IEnumerable<MetadataReference> BasicOnlyReferences()
    {
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(System.Runtime.GCSettings).Assembly.Location);
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

    private static void AssertOutputCompiles(string output, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create(
            "member-desugar-testcontext-verify",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private static void AssertNoResidualProperty(List<MemberDesugarDiagnostic> diags)
    {
        var residual = diags
            .Where(d => d.Code == MemberDesugarDiagnosticCode.ResidualProperty).ToList();
        Assert.True(residual.Count == 0,
            "unexpected CS2J5112 residual-gate diagnostics:\n" +
            string.Join("\n", residual.Select(d => d.ToMsBuildString())));
    }

    // 场景 1：自动属性 + 实例读 + 静态写（ClusterDef.TestContext = x）都必须保持属性语法。
    [Fact]
    public void MstestInjectedTestContext_AutoProperty_RetainedWithPropertySyntaxUseSites()
    {
        const string src = """
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            public class SampleTest
            {
                public TestContext TestContext { get; set; }

                public void Log()
                {
                    this.TestContext.WriteLine("loaded");
                }
            }

            internal static class Holder
            {
                internal static TestContext TestContext { get; set; }

                internal static void Set(TestContext value)
                {
                    Holder.TestContext = value;
                }
            }
            """;

        var (outCode, stats, diags) = Rewrite(src, TestFrameworkStub);

        AssertNoResidualProperty(diags);

        // 反射注入契约：属性声明必须原样保留（展开后属性消失 → 永远为 null）。
        Assert.Contains("public TestContext TestContext { get; set; }", outCode,
            StringComparison.Ordinal);
        Assert.Contains("internal static TestContext TestContext { get; set; }", outCode,
            StringComparison.Ordinal);

        // 使用点保持属性访问语法，绝不残留访问器方法调用。
        Assert.Contains("this.TestContext.WriteLine", outCode, StringComparison.Ordinal);
        Assert.Contains("Holder.TestContext = value;", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_TestContext", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("set_TestContext", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        AssertOutputCompiles(outCode, TestFrameworkStub);
    }

    // 场景 2：显式访问器形态（private 后备字段 + get/set 块）同样保留。
    [Fact]
    public void MstestInjectedTestContext_ExplicitAccessors_Retained()
    {
        const string src = """
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            public class SampleTest
            {
                private TestContext testContextInstance;

                public TestContext TestContext
                {
                    get { return testContextInstance; }
                    set { testContextInstance = value; }
                }

                public void Log()
                {
                    TestContext.WriteLine("loaded");
                }
            }
            """;

        var (outCode, stats, diags) = Rewrite(src, TestFrameworkStub);

        AssertNoResidualProperty(diags);

        Assert.Contains("public TestContext TestContext", outCode, StringComparison.Ordinal);
        Assert.Contains("return testContextInstance;", outCode, StringComparison.Ordinal);
        Assert.Contains("testContextInstance = value;", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_TestContext", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("set_TestContext", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        AssertOutputCompiles(outCode, TestFrameworkStub);
    }

    // 场景 3：CLI 预处理编译只挂 BCL 引用（MSTest 程序集不参与绑定）→ TestContext
    // 退化为 IErrorTypeSymbol。仍须按文件作用域 using 还原名称并保留。
    [Fact]
    public void MstestInjectedTestContext_UnresolvedFrameworkType_RetainedByImportedNamespace()
    {
        const string src = """
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            public class SampleTest
            {
                public TestContext TestContext { get; set; }

                public void Log()
                {
                    this.TestContext.WriteLine("loaded");
                }
            }
            """;

        var (outCode, stats, diags) = RewriteWithReferences(src, BasicOnlyReferences());

        AssertNoResidualProperty(diags);
        Assert.Contains("public TestContext TestContext { get; set; }", outCode,
            StringComparison.Ordinal);
        Assert.Contains("this.TestContext.WriteLine", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_TestContext", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("set_TestContext", outCode, StringComparison.Ordinal);
        Assert.Equal(0, stats.PlansSkipped);
    }

    // 场景 4：项目级 <Using Include="Microsoft.VisualStudio.TestTools.UnitTesting"/>
    // 被预处理链编译成合成 global using 树（源文件自身没有 using 指令）。
    [Fact]
    public void MstestInjectedTestContext_ProjectGlobalUsing_Retained()
    {
        const string src = """
            public class SampleTest
            {
                public TestContext TestContext { get; set; }

                public void Log()
                {
                    TestContext.WriteLine("loaded");
                }
            }
            """;
        const string globalUsings =
            "global using global::Microsoft.VisualStudio.TestTools.UnitTesting;";

        var (outCode, _, diags) = RewriteWithReferences(
            src, BasicOnlyReferences(), globalUsings);

        AssertNoResidualProperty(diags);
        Assert.Contains("public TestContext TestContext { get; set; }", outCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("get_TestContext", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("set_TestContext", outCode, StringComparison.Ordinal);
    }

    // 场景 5（对照）：普通 auto-property 仍照常去糖。
    [Fact]
    public void OrdinaryAutoProperty_StillDesugared()
    {
        const string src = """
            public class Sample
            {
                public int Counter { get; set; }

                public int Read() => Counter;
            }
            """;

        var (outCode, _, _) = Rewrite(src);

        Assert.Contains("get_Counter", outCode, StringComparison.Ordinal);
        Assert.Contains("set_Counter", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Counter { get; set; }", outCode, StringComparison.Ordinal);
    }
}
