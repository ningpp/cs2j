using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C11 属性使用点替换必须保留原表达式的 trivia：语句位赋值被改写成
/// set_X(...) 时，首 token 的 leading trivia 可能挂着 #region/#if 等
/// 预处理器指令。丢失指令会让本地解析校验报 CS1028/CS1038，声明文件回滚
/// 而使用点仍被改写 → 悬空 get_X（真实 SDK：JsonNumberTestData 静态构造器）。
/// </summary>
public class MemberDesugarDirectiveTriviaTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static (string Output, bool Changed, List<MemberDesugarDiagnostic> Diags) Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, "DirectiveTrivia.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-directive-trivia",
            new[] { tree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(tree), stats, diags);
        return (result.OutputCode, result.Changed, diags);
    }

    [Fact]
    public void Property_Assignment_Keeps_Surrounding_Region_Directives()
    {
        const string src = """
            public class Holder
            {
                public static System.Collections.Generic.List<int> Items { get; set; }

                public static void Init()
                {
                    #region build
                    Items = new System.Collections.Generic.List<int>();
                    Items.Add(1);
                    #endregion
                }
            }
            """;
        var (output, changed, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d =>
            d.Code == MemberDesugarDiagnosticCode.PlanRolledBack);
        Assert.Contains("set_Items(", output, StringComparison.Ordinal);
        Assert.Contains("#region", output, StringComparison.Ordinal);
        Assert.Contains("#endregion", output, StringComparison.Ordinal);

        var reparsed = CSharpSyntaxTree.ParseText(output, ParseOptions);
        Assert.DoesNotContain(reparsed.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
    }
}
