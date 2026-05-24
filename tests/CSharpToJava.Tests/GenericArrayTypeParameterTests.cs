using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace CSharpToJava.Tests;

public class GenericArrayTypeParameterTests
{
    [Fact]
    public void MethodCreatingTypeParameterArray_AddsRuntimeClassParameter()
    {
        var result = Convert("""
class Demo {
    public T[] Repeat<T>(T value, int count)
    {
        var result = new T[count];

        for (int i = 0; i < count; i++)
        {
            result[i] = value;
        }

        return result;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public <T> T[] repeat(T value, int count, Class<?> clazz)", code);
        Assert.Contains("(T[]) java.lang.reflect.Array.newInstance(clazz, count)", code);
        Assert.DoesNotContain("new Object[count]", code);
    }

    [Fact]
    public void CallToMethodCreatingTypeParameterArray_AddsClassLiteralArgument()
    {
        var result = Convert("""
class Demo {
    public T[] Repeat<T>(T value, int count)
    {
        return new T[count];
    }
}

class UseDemo {
    public string[] Run()
    {
        return new Demo().Repeat<string>("x", 2);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("repeat(\"x\", 2, String.class)", code);
    }

    [Fact]
    public void GenericCallerForwardsRuntimeClassParameter()
    {
        var result = Convert("""
class Demo {
    public T[] Repeat<T>(T value, int count)
    {
        return new T[count];
    }

    public T[] Twice<T>(T value)
    {
        return Repeat<T>(value, 2);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public <T> T[] twice(T value, Class<?> clazz)", code);
        Assert.Contains("repeat(value, 2, clazz)", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public void SharedGenericArrayFactoryInDenseCallGraph_ConvertsWithoutRepeatedRecursiveScans()
    {
        var source = new StringBuilder("""
class Demo {
    public T[] Repeat<T>(T value, int count)
    {
        return new T[count];
    }

""");

        source.AppendLine("""
    public T[] Wrapper0<T>(T value)
    {
        return Repeat<T>(value, 1);
    }

""");

        for (var i = 1; i < 24; i++)
        {
            var calls = string.Join(Environment.NewLine, Enumerable.Range(0, i).Select(j => $"        Wrapper{j}<T>(value);"));
            source.AppendLine($$"""
    public T[] Wrapper{{i}}<T>(T value)
    {
{{calls}}
        return Repeat<T>(value, {{i + 1}});
    }

""");
        }

        source.AppendLine("}");

        var stopwatch = Stopwatch.StartNew();
        var result = Convert(source.ToString());
        stopwatch.Stop();

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Conversion took {stopwatch.ElapsedMilliseconds} ms");
        var code = result.GeneratedCode!;

        Assert.Contains("public <T> T[] repeat(T value, int count, Class<?> clazz)", code);
        Assert.Contains("public <T> T[] wrapper23(T value, Class<?> clazz)", code);
        Assert.Contains("repeat(value, 24, clazz)", code);
    }

    [Fact]
    public void ClassTypeParameterArrays_AddRuntimeClassFieldsAndConstructorParameters()
    {
        var result = Convert("""
class Demo<K, V> {
    public K[] Keys(int count)
    {
        return new K[count];
    }

    public V[] Values(int count)
    {
        return new V[count];
    }
}

class UseDemo {
    public string[] Run()
    {
        return new Demo<string, int>().Keys(2);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("private final Class<?> kClass;", code);
        Assert.Contains("private final Class<?> vClass;", code);
        Assert.Contains("public Demo(Class<?> kClass, Class<?> vClass)", code);
        Assert.Contains("this.kClass = kClass;", code);
        Assert.Contains("this.vClass = vClass;", code);
        Assert.Contains("java.lang.reflect.Array.newInstance(kClass, count)", code);
        Assert.Contains("java.lang.reflect.Array.newInstance(vClass, count)", code);
        Assert.Contains("new Demo<String, Integer>(String.class, Integer.class).keys(2)", code);
        Assert.DoesNotContain("K.class", code);
        Assert.DoesNotContain("V.class", code);
    }

    [Fact]
    public void ConstructorChaining_ForwardsClassRuntimeParameters()
    {
        var result = Convert("""
class Demo<T> {
    public Demo() : this(1)
    {
    }

    public Demo(int count)
    {
        var result = new T[count];
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public Demo(Class<?> tClass)", code);
        Assert.Contains("this(1, tClass);", code);
        Assert.Contains("public Demo(int count, Class<?> tClass)", code);
        Assert.Contains("this.tClass = tClass;", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public void ParameterizedRuntimeClassArgument_UsesWildcardClassParameter()
    {
        var result = Convert("""
using System.Collections.Generic;

class Box<T> {
    public T[] Make(int count)
    {
        return new T[count];
    }
}

class Demo<K, V> {
    public List<Dictionary<K, V>>[] Run()
    {
        return new Box<List<Dictionary<K, V>>>().Make(1);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("private final Class<?> tClass;", code);
        Assert.Contains("public Box(Class<?> tClass)", code);
        Assert.Contains("new Box<ArrayList<LinkedHashMap<K, V>>>(ArrayList.class).make(1)", code);
        Assert.DoesNotContain("Class<ArrayList<Map<K, V>>>", code);
    }

    [Fact]
    public void ObjectCreationWithConstructorArguments_AppendsClassTokenOnce()
    {
        var result = Convert("""
class Box<T> {
    public Box(int seed)
    {
        var values = new T[seed];
    }
}

class Demo {
    public Box<string> Run()
    {
        return new Box<string>(7);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public Box(int seed, Class<?> tClass)", code);
        Assert.Contains("new Box<String>(7, String.class)", code);
        Assert.DoesNotContain("String.class, String.class", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public void DerivedClassWithImplicitGenericBaseConstructor_PassesConcreteBaseClassToken()
    {
        var result = Convert("""
class Base<T> {
    public Base()
    {
        var values = new T[1];
    }
}

class Derived : Base<string> {
    public Derived()
    {
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public Base(Class<?> tClass)", code);
        Assert.Contains("public Derived()", code);
        Assert.Contains("super(String.class);", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public void GenericDerivedClassWithImplicitGenericBaseConstructor_ForwardsBaseClassToken()
    {
        var result = Convert("""
class Base<T> {
    public Base()
    {
        var values = new T[1];
    }
}

class Derived<T> : Base<T> {
    public Derived()
    {
    }
}

class Demo {
    public Derived<string> Run()
    {
        return new Derived<string>();
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public Derived(Class<?> tClass)", code);
        Assert.Contains("super(tClass);", code);
        Assert.Contains("new Derived<String>(String.class)", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public void FieldInitializerCreatingClassTypeParameterArray_IsMovedToConstructors()
    {
        var result = Convert("""
class Stack<T> {
    private T[] items = new T[8];

    public void Grow()
    {
        items = new T[items.Length * 2];
    }
}

class Demo {
    public Stack<string> Run()
    {
        return new Stack<string>();
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("private T[] items;", code);
        Assert.Contains("public Stack(Class<?> tClass)", code);
        Assert.Contains("this.tClass = tClass;", code);
        Assert.Contains("this.items = (T[]) java.lang.reflect.Array.newInstance(tClass, 8);", code);
        Assert.Contains("new Stack<String>(String.class)", code);
        Assert.DoesNotContain("private T[] items = (T[]) java.lang.reflect.Array.newInstance(tClass, 8);", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public async Task MetadataReferencedGenericBaseConstructor_AppendsRegisteredRuntimeClassTokens()
    {
        var options = new ConversionOptions();
        var libraryCompilation = CreateCompilation("ParserRuntime", """
namespace QUT.Gppg {
    public interface IMerge<T> { }

    public abstract class AbstractScanner<TValue, TSpan> { }

    public abstract class ShiftReduceParser<TValue, TSpan>
        where TSpan : IMerge<TSpan>
    {
        private TValue[] valueStack;
        private TSpan[] locationStack;

        protected ShiftReduceParser(AbstractScanner<TValue, TSpan> scanner)
        {
            valueStack = new TValue[8];
            locationStack = new TSpan[8];
        }
    }
}
""");

        var libraryResults = await new ProjectConversionPipeline(options)
            .ConvertProjectAsync(libraryCompilation);
        Assert.All(libraryResults, result =>
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message))));

        var consumerCompilation = CreateCompilation(
            "Dot2Graph",
            """
using QUT.Gppg;

namespace Dot2Graph {
    public class ValueType { }
    public class LexLocation : IMerge<LexLocation> { }

    public class Parser : ShiftReduceParser<ValueType, LexLocation>
    {
        public Parser() : base(null) { }
    }
}
""",
            EmitMetadataReference(libraryCompilation));

        var consumerResults = await new ProjectConversionPipeline(options)
            .ConvertProjectAsync(consumerCompilation);
        Assert.All(consumerResults, result =>
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message))));

        var code = string.Join("\n", consumerResults.Select(result => result.GeneratedCode));
        Assert.Contains("super(null, LexLocation.class, ValueType.class);", code);
        Assert.DoesNotContain("super(null);", code);
        Assert.DoesNotContain("TValue.class", code);
        Assert.DoesNotContain("TSpan.class", code);
    }

    [Fact]
    public void EmptyTypeParameterArrayReturnedAsIEnumerable_DoesNotNeedRuntimeClassParameter()
    {
        var result = Convert("""
using System.Collections.Generic;

class Demo<T> {
    public IEnumerable<T> All(bool hasItems, IEnumerable<T> items)
    {
        return hasItems ? items : new T[0];
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("Collections.emptyList()", code);
        Assert.DoesNotContain("Class<?>", code);
        Assert.DoesNotContain("Array.newInstance", code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    private static CSharpCompilation CreateCompilation(
        string assemblyName,
        string sourceCode,
        params MetadataReference[] additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: assemblyName + ".cs");
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
        };
        references.AddRange(additionalReferences);

        return CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static MetadataReference EmitMetadataReference(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        stream.Position = 0;
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
