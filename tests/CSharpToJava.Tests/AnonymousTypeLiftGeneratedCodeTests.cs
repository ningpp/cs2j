using CSharpToJava.Core.AnonymousTypeLift;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C21 生成 class 的标识符渲染回归：匿名类型属性名可能恰好是 C# 保留关键字
/// （源码 <c>new { @class = 1 }</c>，符号名去掉 @ 后是 <c>class</c>）。生成的
/// internal sealed class 若原样输出属性名，声明处会直接语法损坏（CS1519/CS1001），
/// 整个中间树无法编译。这里以「改写结果 + 生成文件」重新编译必须零错误的断言锁住。
/// </summary>
public class AnonymousTypeLiftGeneratedCodeTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static Compilation Compile(params SyntaxTree[] trees) =>
        CSharpCompilation.Create(
            "anon-lift-generated-code",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    [Fact]
    public void KeywordNamedProperties_GeneratedTypeFile_Compiles()
    {
        const string src = """
            public static class Probe
            {
                public static object ExplicitKeywordNames() => new { @class = 1, @event = "e" };

                // 驼峰后的构造参数名撞关键字（Class -> class）。
                public static object CamelCasedKeyword() => new { Class = 1, Value = 2 };
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions, path: "Probe.cs");
        var compilation = Compile(tree);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var preprocessor = new AnonymousTypeLiftPreprocessor();
        var statistics = new AnonymousTypeLiftStatistics();
        var diagnostics = new List<AnonymousTypeLiftDiagnostic>();
        var rewritten = preprocessor.RewriteFile(
            src, compilation.GetSemanticModel(tree), statistics, diagnostics);

        Assert.True(
            rewritten.Changed,
            string.Join("; ", diagnostics.Select(d => d.Message)));

        var generated = preprocessor.GenerateTypeFile();
        Assert.NotNull(generated);

        // 成员声明必须补 @，否则生成的 class 无法编译。
        Assert.Contains("public global::System.Int32 @class { get; }", generated);
        Assert.Contains("public global::System.String @event { get; }", generated);
        Assert.Contains("global::System.Int32 class_, global::System.String event_", generated);

        // ToString 标签沿用符号原名（原生匿名类型 ToString 不带 @）。
        Assert.Contains("\"{ class = \"", generated);

        var liftedTree = CSharpSyntaxTree.ParseText(rewritten.OutputCode, ParseOptions, path: "Probe.cs");
        var generatedTree = CSharpSyntaxTree.ParseText(generated!, ParseOptions, path: "_Cs2jAnonymousTypes.cs");
        var lifted = Compile(liftedTree, generatedTree);
        var errors = lifted.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
