using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using Xunit;

namespace CSharpToJava.Tests;

public class LinqAggregateWithSeedParameterRenamingTests
{
    [Fact]
    public void AggregateWithSeed_RenamesBothLambdaParameters()
    {
        var csharp = @"
using System.Linq;
public class MyClass
{
    public string JoinItems(int[] items)
    {
        return items.Aggregate("""", (s, t) => string.Format(""{0},{1}"", s, t));
    }
}";
        var result = ConvertProcedural(csharp);
        var java = result.GeneratedCode;

        // The accumulator 's' should become '_acc', the element 't' should become '_linqitem'
        Assert.Contains("_acc", java);
        Assert.DoesNotContain(" t)", java); // 't' should not remain as-is
        // Should not have undefined variable references
        Assert.Contains("_linqitem", java);
    }

    [Fact]
    public void AggregateWithSeed_AssignmentExpression_RenamesBothParameters()
    {
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class LinkedPoint
{
    public Point Point;
    public LinkedPoint Next;
    public LinkedPoint(Point p) { Point = p; }
}

public class Point { }

public class MyClass
{
    public LinkedPoint Build(IEnumerable<Point> pathPoints)
    {
        var ret = new LinkedPoint(pathPoints.First());
        pathPoints.Skip(1).Aggregate(ret, (lp, p) => lp.Next = new LinkedPoint(p));
        return ret;
    }
}";
        var result = ConvertProcedural(csharp);
        var java = result.GeneratedCode;

        // 'lp' (accumulator) → _acc, 'p' (element) → _linqitem
        Assert.Contains("_acc", java);
        // The body should use _acc.Next and new LinkedPoint(_linqitem)
        Assert.Contains("_acc.Next", java);
        Assert.Contains("new LinkedPoint(_linqitem)", java);
    }

    [Fact]
    public void AggregateWithSeed_StaticMethodGroupOnArray_ProducesProcedural()
    {
        var csharp = @"
using System.Linq;

public static class PathHelper
{
    public static string Combine(string left, string right) => left + ""/"" + right;
}

public class MyClass
{
    public string Build(string[] parts, string root)
    {
        return parts.Aggregate(root, PathHelper.Combine);
    }
}";
        var result = ConvertProcedural(csharp);
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.Contains("ProceduralLinq", java);
        Assert.Contains("PathHelper.combine(_acc, _linqitem)", java);
        Assert.DoesNotContain(".aggregate(", java, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateWithSeed_ErrorStringSeedSpecialType_NamesStringSeedInRewrittenHelper()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "LinearSystemSolver.cs");
        var syntaxTree = CSharpSyntaxTree.ParseText("""
using System;
using System.Linq;

namespace Microsoft.Msagl.Core.Layout.ProximityOverlapRemoval.ConjugateGradient
{
    public class LinearSystemSolver
    {
        public static void Test(double[] result1)
        {
            string res = result1.Aggregate("", (s, t) => string.Format("{0},\t{1}", s, t));
        }
    }
}
""", path: sourcePath);
        var compilation = CSharpCompilation.Create(
            "AutomaticGraphLayout",
            new[] { syntaxTree },
            references: Array.Empty<MetadataReference>(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync();
        var aggregateInvocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(inv => inv.Expression.ToString().Contains(".Aggregate", StringComparison.Ordinal));
        var seedExpression = aggregateInvocation.ArgumentList.Arguments[0].Expression;
        var seedType = semanticModel.GetTypeInfo(seedExpression);
        Assert.IsAssignableFrom<IErrorTypeSymbol>(seedType.Type);
        Assert.Equal(SpecialType.System_String, seedType.Type?.SpecialType);

        // C26 M4-c：项目路径内嵌 pass 已摘除，但该回归守护的逻辑全部位于保留的
        // LinqRewriter（linq-channel 与单文件路径共用）。即使编译没有任何
        // 元数据引用（seed 表达式是 IErrorTypeSymbol 但 SpecialType=String），
        // 抽出的过程化 helper 的 seed 形参仍必须推断为 string，而非 object。
        var rewriter = new LinqRewriter(semanticModel, CreateProjectOptions());
        var rewritten = rewriter.Visit(root)!.NormalizeWhitespace().ToFullString();

        Assert.Contains("Test_ProceduralLinq1", rewritten, StringComparison.Ordinal);
        Assert.Contains("string _seed", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("object _seed", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateWithSeed_MSTestConditionalAccessSeedKeepsStringHelperType()
    {
        const string source = @"
using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

public class MyClass
{
    public TestContext TestContext { get; set; }

    public string Build(params string[] parts)
    {
        var baseDirectory = TestContext?.DeploymentDirectory;
        if (string.IsNullOrEmpty(baseDirectory)) {
            baseDirectory = Path.Combine(TestContext?.TestRunDirectory ?? AppContext.BaseDirectory, ""Out"");
        }
        return parts.Aggregate(baseDirectory, Path.Combine);
    }
}";
        // C26 M4-c：项目路径内嵌 pass 已摘除；该回归守护的 LinqRewriter seed 类型
        // 推断在保留的单文件过程化路径（同一 LinqRewriter）上验证：条件访问
        // seed 的 helper 返回类型与 _seed 形参都必须是 String，不得退化为 Object。
        var result = ConvertProcedural(source);
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.Contains("String build_ProceduralLinq1", java, StringComparison.Ordinal);
        Assert.Contains("String _seed", java, StringComparison.Ordinal);
        Assert.DoesNotContain("Object build_ProceduralLinq1", java, StringComparison.Ordinal);
        Assert.DoesNotContain("Object _seed", java, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateWithSeed_GetMethodFullNameUsesSeedOverloadWhenSyntaxHasSeedArgument()
    {
        var source = @"
using System.Linq;

public static class PathHelper
{
    public static string Combine(string left, string right) => left + ""/"" + right;
}

public class MyClass
{
    public string Build(string root, params string[] parts)
    {
        return parts.Aggregate(root, PathHelper.Combine);
    }
}";
        var (rewriter, aggregateInvocation) = CreateRewriterWithoutLinqReferences(source);
        var methodName = InvokeGetMethodFullName(rewriter, aggregateInvocation);

        Assert.Equal(
            "System.Collections.Generic.IEnumerable<TSource>.Aggregate<TSource, TAccumulate>(TAccumulate, System.Func<TAccumulate, TSource, TAccumulate>)",
            methodName);
    }

    private static ConversionResult ConvertProcedural(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                PreferStreamApi = false,
            },
        });
    }

    private static ConversionOptions CreateProjectOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };
    }

    private static (LinqRewriter Rewriter, InvocationExpressionSyntax AggregateInvocation) CreateRewriterWithoutLinqReferences(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(string).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "AggregateFallbackTest",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semantic = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(node => node.Expression is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name.Identifier.Text == "Aggregate");

        return (new LinqRewriter(semantic, new ConversionOptions()), invocation);
    }

    private static string? InvokeGetMethodFullName(LinqRewriter rewriter, InvocationExpressionSyntax invocation)
    {
        var method = typeof(LinqRewriter).GetMethod("GetMethodFullName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method.Invoke(rewriter, new object[] { invocation });
    }
}
