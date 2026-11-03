using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C11 后备字段命名的 C# 合法性：camelCase 后的属性名可能撞 C# 保留关键字。
/// Java 关键字转义只覆盖 Java 侧（Int → intValue），String/Object/Base 这类
/// 仅在 C# 保留的单词会原样成为字段名（真实 SDK：`public string string;`），
/// 本地解析校验失败后声明文件回滚而使用点仍被改写 → CS1061 get_Int 悬空。
/// </summary>
public class MemberDesugarKeywordBackingFieldTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static (string Output, bool Changed, List<MemberDesugarDiagnostic> Diags) Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, "KeywordFields.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-keyword-fields",
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
    public void Property_Names_Matching_CSharp_Keywords_Get_Legal_Backing_Fields()
    {
        const string src = """
            public class ClassWrapper
            {
                public string @string { get; set; }
                public object @object { get; set; }
                public ClassWrapper(string stringArgument, object objectArgument)
                {
                    @string = stringArgument;
                    @object = objectArgument;
                }
            }
            """;
        var (output, changed, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d =>
            d.Code == MemberDesugarDiagnosticCode.PlanRolledBack);
        Assert.Contains("stringValue", output, StringComparison.Ordinal);
        Assert.Contains("objectValue", output, StringComparison.Ordinal);

        // 输出必须是合法 C#：关键字不能作字段名。
        var reparsed = CSharpSyntaxTree.ParseText(output, ParseOptions);
        Assert.DoesNotContain(reparsed.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
    }
}
