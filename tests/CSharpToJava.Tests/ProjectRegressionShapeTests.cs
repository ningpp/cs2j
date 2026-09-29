using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.IO;

namespace CSharpToJava.Tests;

public class ProjectRegressionShapeTests
{
    [Fact]
    public void ExplicitSingleParameterLambda_EmitsParenthesizedJavaParameter()
    {
        var result = Convert("""
using System;

class PropertyInfo
{
    public bool IsStatic { get; set; }
}

class Sample
{
    private static readonly Func<PropertyInfo, bool> IsInstance = (PropertyInfo property) => !property.IsStatic;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("(PropertyInfo property) ->", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyInfo property ->", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsuffixedUlongBoundaryLiteral_EmitsJavaLongParseExpression()
    {
        var result = Convert("""
class Sample
{
    bool IsLongMinValueMagnitude(ulong value)
    {
        return value == 9223372036854775808;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Long.parseUnsignedLong(\"9223372036854775808\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("== 9223372036854775808", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalAccess_MethodInvocation_EmitsSingleJavaCall()
    {
        var result = Convert("""
class Sample
{
    int HashOrZero(string value)
    {
        return value?.GetHashCode() ?? 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("value.hashCode()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("hashCode()()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalAccess_MethodInvocation_WithoutSemanticModel_UsesMethodNameMapping()
    {
        var expression = (ConditionalAccessExpressionSyntax)SyntaxFactory.ParseExpression("value?.GetHashCode()");
        var context = new ConversionContext(new ConversionOptions(), new TypeMappingRegistry(new TypeMappingConfig()));

        var generated = ExpressionTransformerFacade.Instance.Transform(expression, context);

        Assert.Contains("value.hashCode()", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("getGetHashCode", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("()()", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionOperator_ToOptionalTarget_EmitsLegalJavaMethodName()
    {
        var result = Convert("""
class Sample
{
    private readonly int value;

    public static explicit operator int?(Sample node)
    {
        return node.value;
    }
}
""", configure: options => options.UseOptionalForNullable = true);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("toOptionalInt(Sample node)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toOptional<int>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toOptional<", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleTypeField_MapsToVavrTupleType()
    {
        var result = Convert("""
class Sample
{
    private (bool HasMatch, object Value) cache;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Tuple2<Boolean, Object> cache", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(bool HasMatch, object Value)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticNamedTupleField_MapsToVavrTupleType()
    {
        var result = Convert("""
class Sample
{
    private (bool HasMatch, object Value) cache = (false, null);
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Tuple2<Boolean, Object> cache", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Boolean HasMatch, Object Value)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameNameDelegatesWithDifferentArity_UseAritySafeJavaNames()
    {
        var delegatesFile = """
namespace Demo
{
    public delegate TResult Factory<TBase, TResult>(TBase wrapped) where TResult : TBase;
    public delegate TResult Factory<TArg, TBase, TResult>(TBase wrapped, TArg arg) where TResult : TBase;
}
""";

        var consumerFile = """
namespace Demo
{
    public class Consumer
    {
        private Factory<object, string> one;
        private Factory<int, object, string> two;
    }
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[]
                {
                    new SourceFile { FilePath = "Factories.cs", Content = delegatesFile },
                    new SourceFile { FilePath = "Consumer.cs", Content = consumerFile },
                },
                projectName: "DelegateArityCollision");

        var factory2 = Assert.Single(results, r => r.FileName == "Factory2.java");
        Assert.True(factory2.Success, string.Join("\n", factory2.Diagnostics.Select(d => d.Message)));
        Assert.Contains("interface Factory2<TBase, TResult extends TBase>", factory2.GeneratedCode, StringComparison.Ordinal);

        var factory3 = Assert.Single(results, r => r.FileName == "Factory3.java");
        Assert.True(factory3.Success, string.Join("\n", factory3.Diagnostics.Select(d => d.Message)));
        Assert.Contains("interface Factory3<TArg, TBase, TResult extends TBase>", factory3.GeneratedCode, StringComparison.Ordinal);

        var consumer = Assert.Single(results, r => r.FileName == "Consumer.java");
        Assert.True(consumer.Success, string.Join("\n", consumer.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Factory2<Object, String> one", consumer.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Factory3<Integer, Object, String> two", consumer.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Factory<", consumer.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Factory.java", string.Join("\n", results.Select(r => r.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullyQualifiedExpressionFunc_MapsInnerFuncToJavaFunction()
    {
        var source = """
namespace Demo
{
    using System;

    internal abstract class Builder<TBuilder>
    {
        public abstract TBuilder WithAttributeOverride<TClass>(
            System.Linq.Expressions.Expression<Func<TClass, object>> propertyAccessor,
            System.Attribute attribute);
    }
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json")
        }).ConvertProjectAsync(
            new[]
            {
                new SourceFile { FilePath = "Builder.cs", Content = source },
            },
            projectName: "ExpressionFuncMapping");

        var builder = Assert.Single(results, r => r.FileName == "Builder.java");
        Assert.True(builder.Success, string.Join("\n", builder.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Function<TClass, Object> propertyAccessor", builder.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Linq.Expressions.Function", builder.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticExpressionFunc_DoesNotQualifyMappedFunctionWithExpressionNamespace()
    {
        var source = """
using System;
using System.Linq.Expressions;

namespace Demo
{
    internal abstract class Builder<TBuilder>
    {
        public abstract TBuilder With<TClass>(Expression<Func<TClass, object>> accessor, Attribute attribute);
    }
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json")
        }).ConvertProjectAsync(
            new[]
            {
                new SourceFile { FilePath = "Builder.cs", Content = source },
            },
            projectName: "SemanticExpressionFuncMapping");

        var builder = Assert.Single(results, r => r.FileName == "Builder.java");
        Assert.True(builder.Success, string.Join("\n", builder.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Function<TClass, Object> accessor", builder.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Linq.Expressions.Function", builder.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Expressions.Function", builder.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectCompilationBuilder_ResolvesSystemLinqExpressions()
    {
        var source = """
using System;
using System.Linq.Expressions;

class Sample
{
    void Use(Expression<Func<Sample, object>> accessor) { }
}
""";

        var context = new ConversionContext(new ConversionOptions(), new TypeMappingRegistry(new TypeMappingConfig()));
        var compilation = ProjectCompilationBuilder.BuildCompilation(
            new[]
            {
                new SourceFile { FilePath = "Sample.cs", Content = source },
            },
            context);

        Assert.NotNull(compilation);
        var tree = Assert.Single(compilation.SyntaxTrees, t => t.FilePath == "Sample.cs");
        var model = compilation.GetSemanticModel(tree);
        var parameterType = tree.GetRoot()
            .DescendantNodes()
            .OfType<ParameterSyntax>()
            .Single()
            .Type!;

        var resolvedType = model.GetTypeInfo(parameterType).Type;
        var namedType = Assert.IsAssignableFrom<Microsoft.CodeAnalysis.INamedTypeSymbol>(resolvedType);
        Assert.Equal("Expression", namedType.Name);
        Assert.Equal("System.Linq.Expressions", namedType.ContainingNamespace.ToDisplayString());
    }
    private static ConversionResult Convert(
        string sourceCode,
        bool useDefaultMappings = false,
        Action<ConversionOptions>? configure = null)
    {
        var options = new ConversionOptions
        {
            TypeMappingConfigPath = useDefaultMappings
                ? Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json")
                : null,
        };
        configure?.Invoke(options);

        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = options,
        });
    }
}
