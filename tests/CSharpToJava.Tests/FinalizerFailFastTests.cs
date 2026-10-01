using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// FN-9（C39）：终结器 ~T() 必须先经 member-desugar 终结器规则处置，漏网进入
/// Java 转换器时快速失败；IncompleteMember 不再静默丢弃，显式 Warning（CS2J1007）。
/// </summary>
public class FinalizerFailFastTests
{
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

    [Fact]
    public void Destructor_ReachingClassTransformer_FailsFastWithC39Guidance()
    {
        var result = Convert("""
            class Sample
            {
                ~Sample() { }
            }
            """);

        Assert.False(result.Success);
        var message = string.Join(" | ", result.Diagnostics.Select(d => d.Message));
        Assert.Contains("CS2J normalization required", message, StringComparison.Ordinal);
        Assert.Contains("finalizer", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("~Sample", message, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteMember_IsWarned_NotSilentlyDropped()
    {
        // public int; 无法构成有效成员声明，Roslyn 解析为 IncompleteMemberSyntax（伴随 CS1519，
        // 端到端管线会在解析闸门短路），故此处直接调用 ClassTransformer 验证分发行为。
        const string code = """
            class Sample
            {
                public int;
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("Sample", new[] { tree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            });
        var context = new ConversionContext(new ConversionOptions(),
            new TypeMappingRegistry(new TypeMappingConfig()))
        {
            ProjectCompilation = compilation,
            SemanticModel = compilation.GetSemanticModel(tree),
        };
        var classNode = tree.GetCompilationUnitRoot().Members.OfType<ClassDeclarationSyntax>().Single();

        new ClassTransformer().Transform(classNode, context);

        var warning = Assert.Single(context.Diagnostics.Messages,
            d => d.Severity == CSharpToJava.Core.Context.DiagnosticSeverity.Warning
                && d.Code == "CS2J1007");
        Assert.Equal("IncompleteMember", warning.Category);
        Assert.Contains("Incomplete member", warning.Message, StringComparison.Ordinal);
    }
}
