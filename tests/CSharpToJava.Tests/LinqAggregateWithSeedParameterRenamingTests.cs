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
    public async Task ProjectAggregateWithSeed_SystemPathCombineMethodGroupOnParamsArray_ProducesProcedural()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = @"
using System.IO;
using System.Linq;

public class MyClass
{
    public string Build(string root, params string[] parts)
    {
        return parts.Aggregate(root, Path.Combine);
    }
}",
            },
        });

        var result = Assert.Single(results, item => item.FileName == "MyClass.java");
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.Contains("ProceduralLinq", java);
        Assert.Contains("java.nio.file.Paths.get(_acc, _linqitem).toString()", java);
        Assert.DoesNotContain(".aggregate(", java, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectAggregateWithSeed_ErrorStringSeedSpecialType_UsesStringSeedParameter()
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

        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(
            compilation,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourcePath },
            "AutomaticGraphLayout");

        var result = Assert.Single(results, item => item.FileName == "LinearSystemSolver.java");
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.True(java.Contains("String test_ProceduralLinq", StringComparison.Ordinal), java);
        Assert.True(java.Contains("String _seed", StringComparison.Ordinal), java);
        Assert.DoesNotContain("? _seed", java, StringComparison.Ordinal);
        Assert.DoesNotContain("Optional<Object> test_ProceduralLinq", java, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectAggregateWithSeed_MSTestConditionalAccessSeedKeepsStringHelperType()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = @"
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
}",
            },
        });

        var result = Assert.Single(results, item => item.FileName == "MyClass.java");
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.True(java.Contains("String build_ProceduralLinq1", StringComparison.Ordinal), java);
        Assert.Contains("String _seed", java);
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
