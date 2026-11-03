using CSharpToJava.CLI;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.MemberDesugar;

/// <summary>
/// 回归：整树合并编译把各工程的 <c>global using</c> 合并到同一编译，其它工程的
/// <c>global using dotnet.text.json;</c> 会让 xml-tiny 的 <c>MS.Internal.Xml.XPath</c>
/// 文件里的简单名 <c>SR</c> 与 <c>dotnet.text.json.SR</c> 二义（CS0104），
/// 成员访问完全绑定不到符号。若不回退解析，SR 属性声明被展开后使用点仍留在
/// <c>SR.Xp_InvalidPattern</c>，msbuild 报 CS0117（真实 SDK e37 的 92 条错误）。
/// 回退仅在符号完全未绑定时按「接收者候选类型 + 成员名」唯一命中。
/// </summary>
public class MemberDesugarAmbiguousSiteBindingTests
{
    private const string SrFile = """
        namespace dotnet.xml
        {
            internal static class SR
            {
                internal static string Xp_InvalidPattern => "invalid pattern";
            }
        }
        """;

    /// <summary>模拟 json 测试工程的 GlobalUsings.cs：跨工程泄漏到合并编译。</summary>
    private const string JsonGlobalUsingsFile = """
        global using dotnet.text.json;

        namespace dotnet.text.json
        {
            internal static class SR
            {
                internal static string Unrelated => "unrelated";
            }
        }
        """;

    private const string ConsumerFile = """
        using dotnet.xml;

        namespace MS.Internal.Xml.XPath
        {
            internal class DescendantBaseQuery
            {
                internal void MatchNode()
                {
                    throw new System.InvalidOperationException(SR.Xp_InvalidPattern);
                }
            }
        }
        """;

    [Fact]
    public void AmbiguousSimpleNameReceiver_SiteIsPlannedAndRewritten()
    {
        var trees = new[]
        {
            (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                SrFile, new CSharpParseOptions(LanguageVersion.Latest), path: "xml-tiny/src/Resources/SR.cs"),
            (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                JsonGlobalUsingsFile, new CSharpParseOptions(LanguageVersion.Latest),
                path: "json/tests/GlobalUsings.cs"),
            (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                ConsumerFile, new CSharpParseOptions(LanguageVersion.Latest),
                path: "xml-tiny/src/System/Xml/XPath/Internal/DescendantBaseQuery.cs"),
        };
        var compilation = CSharpCompilation.Create(
            "member-desugar-ambiguous",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor(new MemberDesugarOptions());
        preprocessor.BindCompilation(compilation);

        var plan = preprocessor.Plans
            .OfType<PropertyPlan>()
            .Single(p => p.DeclName == "Xp_InvalidPattern");
        Assert.Contains(plan.TouchedFiles,
            f => f.EndsWith("DescendantBaseQuery.cs", StringComparison.Ordinal));

        var statistics = new MemberDesugarStatistics();
        var diagnostics = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            trees[2].ToString(), compilation.GetSemanticModel(trees[2]), statistics, diagnostics);

        Assert.True(result.Changed, "使用点文件未被改写：" + string.Join("; ", diagnostics));
        Assert.DoesNotContain("SR.Xp_InvalidPattern", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("SR.get_Xp_InvalidPattern()", result.OutputCode, StringComparison.Ordinal);
    }
}
