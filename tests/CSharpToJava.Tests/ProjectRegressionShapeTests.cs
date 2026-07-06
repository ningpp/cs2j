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
    public void GenericOfType_WithQualifiedTypeArgument_UsesMappedTypeName()
    {
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;

namespace Outer.Inner
{
    public class Marker { }
}

class Sample
{
    object FirstMarker(IEnumerable<object> items)
    {
        return items.OfType<Outer.Inner.Marker>().FirstOrDefault();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("instanceof Marker", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(Marker) x", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO: QualifiedName", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericOfType_WithPackageQualifiedMappedType_PreservesQualifier()
    {
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

class Sample
{
    object FirstProperty(IEnumerable<object> items)
    {
        return items.OfType<PropertyInfo>().FirstOrDefault();
    }
}
""", useDefaultMappings: true);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("instanceof PropertyInfo", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(PropertyInfo) x", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.PropertyInfo;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Reflection", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullCoalesceAssignment_StandaloneMemberAccess_DoesNotEmitBareReadStatement()
    {
        var result = Convert("""
class Helper
{
    public static object Instance { get; set; }
}

class DefaultHelper
{
}

class Sample
{
    void Build()
    {
        Helper.Instance ??= new DefaultHelper();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("if (__nullCoalTemp", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".Instance;\n", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullCoalesceAssignment_InVoidLambda_DoesNotEmitBareReadStatement()
    {
        var result = Convert("""
using System;

class Helper
{
    public static object Instance { get; set; }
}

class DefaultHelper
{
}

class Sample
{
    void Build()
    {
        Action action = () => Helper.Instance ??= new DefaultHelper();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("if (__nullCoalTemp", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".Instance;\n", result.GeneratedCode, StringComparison.Ordinal);
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
    public void BclFallback_DoesNotEmitPhantomDotnetSystemPackages()
    {
        var result = Convert("""
using System;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

class Sample
{
    void Use(IDictionary dictionary, IList list, Type type, Expression<Func<Sample, object>> accessor)
    {
        var info = type.GetTypeInfo();
    }
}
""", useDefaultMappings: true);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("dotnet.system.Collections", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Linq.Expressions", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Reflection", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpIDictionary dictionary", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpGenericIList<?> list", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Function<Sample, Object>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UriMapping_UsesCompatPackageInsteadOfPhantomDotnetSystem()
    {
        var result = Convert("""
using System;

class Sample
{
    bool Check(string value)
    {
        return Uri.IsWellFormedUriString(value, UriKind.RelativeOrAbsolute);
    }
}
""", useDefaultMappings: true);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("dotnet.system.Uri", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Uri.isWellFormedUriString(value, UriKind.RelativeOrAbsolute)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Uri.isWellFormedUriString", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.UriKind;", result.GeneratedCode, StringComparison.Ordinal);
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
    public void GenericTypeArgument_WithNamedTuple_MapsTupleArgument()
    {
        var result = Convert("""
using System.Collections.Concurrent;

class Sample
{
    private ConcurrentDictionary<System.Type, (bool HasMatch, string Value)> cache = new();
}
""", useDefaultMappings: true);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("ConcurrentHashMap<Class, Tuple2<Boolean, String>> cache", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(bool HasMatch", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyedCollectionBaseType_MapsToCompatType()
    {
        var source = """
using System.Collections.ObjectModel;

class Item
{
    public string Key { get; set; }
}

class Items : KeyedCollection<string, Item>
{
    protected override string GetKeyForItem(Item item) => item.Key;
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json")
        }).ConvertProjectAsync(
            new[]
            {
                new SourceFile { FilePath = "Items.cs", Content = source },
            },
            projectName: "KeyedCollectionMapping");

        var items = Assert.Single(results, r => r.FileName == "Items.java");
        Assert.True(items.Success, string.Join("\n", items.Diagnostics.Select(d => d.Message)));
        Assert.Contains("extends KeyedCollection<String, Item>", items.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.KeyedCollection;", items.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections.ObjectModel", items.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameNameGenericAndNonGenericTypes_UseAritySafeJavaNames()
    {
        var poolFile = """
namespace Demo
{
    internal abstract class Pool<T> where T : class
    {
        public abstract T Get();
        public abstract void Return(T item);
    }

    internal static class Pool
    {
        public static Pool<T> Create<T>() where T : class, new()
        {
            return new DefaultPool<T>();
        }
    }
}
""";

        var defaultPoolFile = """
namespace Demo
{
    internal class DefaultPool<T> : Pool<T> where T : notnull
    {
        private protected T? fastItem;

        public override T Get()
        {
            return fastItem!;
        }

        public override void Return(T item)
        {
            fastItem = item;
        }
    }
}
""";

        var consumerFile = """
namespace Demo
{
    internal static class Consumer
    {
        private static readonly Pool<string> Items = Pool.Create<string>();
    }
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[]
                {
                    new SourceFile { FilePath = "Pool.cs", Content = poolFile },
                    new SourceFile { FilePath = "DefaultPool.cs", Content = defaultPoolFile },
                    new SourceFile { FilePath = "Consumer.cs", Content = consumerFile },
                },
                projectName: "ArityCollision");

        var genericPool = Assert.Single(results, r => r.FileName == "Pool1.java");
        Assert.True(genericPool.Success, string.Join("\n", genericPool.Diagnostics.Select(d => d.Message)));
        Assert.Contains("abstract class Pool1<T>", genericPool.GeneratedCode, StringComparison.Ordinal);

        var factoryPool = Assert.Single(results, r => r.FileName == "Pool.java");
        Assert.True(factoryPool.Success, string.Join("\n", factoryPool.Diagnostics.Select(d => d.Message)));
        Assert.Contains("public final class Pool", factoryPool.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Pool1<T> create", factoryPool.GeneratedCode, StringComparison.Ordinal);

        var defaultPool = Assert.Single(results, r => r.FileName == "DefaultPool.java");
        Assert.True(defaultPool.Success, string.Join("\n", defaultPool.Diagnostics.Select(d => d.Message)));
        Assert.Contains("extends Pool1<T>", defaultPool.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("protected T fastItem", defaultPool.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("extends notnull", defaultPool.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("protected private", defaultPool.GeneratedCode, StringComparison.Ordinal);

        var consumer = Assert.Single(results, r => r.FileName == "Consumer.java");
        Assert.True(consumer.Success, string.Join("\n", consumer.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Pool1<String> Items", consumer.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Pool.create", consumer.GeneratedCode, StringComparison.Ordinal);
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

    [Fact]
    public async Task ReflectionComponentModelAndExpressionBclTypes_MapToCompatTypes()
    {
        var source = """
using System;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

namespace Demo
{
    public class Sample
    {
        public LambdaExpression Accessor { get; set; }
        public MemberInfo Member { get; set; }
        public TypeInfo Info { get; set; }
        public TypeCode Code { get; set; }
        public TypeConverter Converter { get; set; }
        public TypeConverterAttribute Attribute { get; set; }

        public void Use(Type type)
        {
            Info = type.GetTypeInfo();
            Code = Type.GetTypeCode(type);
            Converter = TypeDescriptor.GetConverter(type);
            TypeDescriptor.AddAttributes(type, new TypeConverterAttribute(typeof(Sample)));
        }
    }
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json")
        }).ConvertProjectAsync(
            new[]
            {
                new SourceFile { FilePath = "Sample.cs", Content = source },
            },
            projectName: "BclCompatMapping");

        var sample = Assert.Single(results, r => r.FileName == "Sample.java");
        Assert.True(sample.Success, string.Join("\n", sample.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import io.github.ningpp.compat.LambdaExpression;", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.MemberInfo;", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeInfo;", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeCode;", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ComponentModelTypeConverter;", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeConverterAttribute;", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeDescriptor;", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper;", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("TypeDescriptor.getConverter(type)", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("TypeDescriptor.addAttributes(type", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("TypeHelper.getTypeCode(type)", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.ComponentModel", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Linq.Expressions", sample.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Class.getTypeCode", sample.GeneratedCode, StringComparison.Ordinal);
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
