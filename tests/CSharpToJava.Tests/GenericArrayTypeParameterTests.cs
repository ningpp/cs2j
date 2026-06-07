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

    [Fact]
    public void LinqToArrayReturningClassTypeParameterArray_UsesRuntimeClassField()
    {
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;

class Tree<T> {
    public T[] GetAllIntersecting(IEnumerable<T> items)
    {
        return items.ToArray();
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("private final Class<?> tClass;", code);
        Assert.Contains("public Tree(Class<?> tClass)", code);
        Assert.Contains("java.lang.reflect.Array.newInstance(tClass, size)", code);
        Assert.DoesNotContain("new Object[size]", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public void LinqToArrayReturningMethodTypeParameterArray_UsesRuntimeClassParameter()
    {
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;

class Demo {
    public T[] Copy<T>(IEnumerable<T> items)
    {
        return items.ToArray();
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public <T> T[] copy(Iterable<T> items, Class<?> clazz)", code);
        Assert.Contains("java.lang.reflect.Array.newInstance(clazz, size)", code);
        Assert.DoesNotContain("new Object[size]", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public void ConstructorChaining_ThisWithNoArgs_DoesNotGenerateTClass()
    {
        // ArrayBuilder(int capacity) : this() — the parameterless ctor doesn't create arrays,
        // but it still gets Class<?> parameter because the tClass field is private final.
        // The key fix: this() should use tClass (parameter name), NOT T.class (invalid Java).
        var result = Convert("""
class ArrayBuilder<T> {
    private T[] items;
    private int count;

    public ArrayBuilder()
    {
        count = 0;
    }

    public ArrayBuilder(int capacity) : this()
    {
        items = new T[capacity];
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Both constructors get Class<?> parameter (tClass field is final, must be initialized)
        Assert.Contains("public ArrayBuilder(Class<?> tClass)", code);
        Assert.Contains("public ArrayBuilder(int capacity, Class<?> tClass)", code);
        // this() should pass tClass (parameter name), NOT T.class (invalid Java)
        Assert.Contains("this(tClass);", code);
        Assert.DoesNotContain("this(T.class)", code);
        Assert.DoesNotContain("T.class", code);
    }

    [Fact]
    public void NestedClassSelfReference_UsesSimpleNameNotFQN()
    {
        // When a non-generic nested class (Entry) inside a generic outer class
        // references itself (e.g., Entry _next), the generated Java code should
        // use the simple name "Entry<TKey, TValue>" not the FQN
        // "pkg.Outer.Entry<TKey, TValue>".
        var result = Convert("""
using System.Collections.Generic;

class LowLevelDictionary<TKey, TValue> {
    private Entry[] _buckets;

    private Entry find(TKey key)
    {
        return null;
    }

    private class Entry
    {
        public TKey _key;
        public TValue _value;
        public Entry _next;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Entry should NOT have FQN prefix like "LowLevelDictionary.Entry"
        Assert.DoesNotContain("LowLevelDictionary.Entry", code);
        // Entry should be referenced with type parameters
        Assert.Contains("Entry<TKey, TValue>", code);
    }

    [Fact]
    public void StructWithGenericArrayCreation_AddsRuntimeClassParameter()
    {
        // C# struct with new T[] should get Class<?> parameter in Java,
        // just like classes do. The StructTransformer was missing this.
        var result = Convert("""
struct ArrayBuilder<T> {
    private T[] _array;
    private int _count;

    public ArrayBuilder(int capacity)
    {
        _count = 0;
        _array = new T[capacity];
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Struct should get Class<?> field and constructor parameter
        Assert.Contains("private final Class<?> tClass;", code);
        Assert.Contains("public ArrayBuilder(int capacity, Class<?> tClass)", code);
        // Array creation should use tClass
        Assert.Contains("java.lang.reflect.Array.newInstance(tClass", code);
        // clone() should pass tClass to constructor
        Assert.Contains("new ArrayBuilder<>(this.tClass)", code);
        // Should NOT have "this.tClass = null;" (the old bug)
        Assert.DoesNotContain("this.tClass = null;", code);
    }

    [Fact]
    public void CustomTypeToArray_DoesNotAddGeneratorArgument()
    {
        // Custom types in System.Collections.Generic namespace that define their own
        // ToArray() should NOT get the Java Collection.toArray(IntFunction) pattern.
        var result = Convert("""
using System.Collections.Generic;

namespace System.Collections.Generic {
    struct ArrayBuilder<T> {
        private T[] _array;

        public T[] ToArray()
        {
            return _array;
        }
    }
}

class Demo {
    public string[] Run(ArrayBuilder<string> builder)
    {
        return builder.ToArray();
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // ToArray() should be called without generator argument
        Assert.Contains("builder.toArray()", code);
        // Should NOT have String[]::new argument
        Assert.DoesNotContain("String[]::new", code);
    }

    [Fact]
    public void ThrowExpressionInIntTernary_UsesSupplierInteger()
    {
        // C# throw expressions in ternary operators need Supplier<Integer> (not Supplier<Object>)
        // so the ternary type is compatible with int.
        var result = Convert("""
using System;

class Demo {
    public static int FromHex(char digit) =>
        (uint)(digit - '0') <= '9' - '0' ? digit - '0' :
        (uint)(digit - 'A') <= 'F' - 'A' ? digit - 'A' + 10 :
        (uint)(digit - 'a') <= 'f' - 'a' ? digit - 'a' + 10 :
        throw new ArgumentException(nameof(digit));
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Should use Supplier<Integer> for int-returning ternary
        Assert.Contains("Supplier<Integer>", code);
        // Should NOT use Supplier<Object> (which causes type mismatch)
        Assert.DoesNotContain("Supplier<Object>", code);
    }

    [Fact]
    public void StringCreate_ConvertsToStringHelperCreateString()
    {
        // C# string.Create<TState>(int length, TState state, SpanAction<char, TState> action)
        // should convert to StringHelper.createString(length, state, action)
        var result = Convert("""
using System;

class Demo {
    public static string HexEscape(char character)
    {
        return string.Create(3, character, (Span<char> chars, char c) =>
        {
            chars[0] = '%';
            chars[1] = 'A';
            chars[2] = 'B';
        });
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Should use StringHelper.createString
        Assert.Contains("StringHelper.createString(", code);
        // Should NOT use String.create (which doesn't exist in Java)
        Assert.DoesNotContain("String.create(", code);
    }

    [Fact]
    public void SwitchOnLong_ConvertsToIfElse()
    {
        // C# switch on long type should convert to if-else since Java doesn't support switch(long)
        var result = Convert("""
using System;

class Demo {
    public static string GetHostType(long hostType)
    {
        switch (hostType)
        {
            case 0x00010000L: return "IPv6";
            case 0x00020000L: return "IPv4";
            case 0x00030000L: return "Dns";
            default: return "Unknown";
        }
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Should use if-else instead of switch
        Assert.Contains("if (", code);
        Assert.Contains("else if (", code);
        // Should NOT contain switch statement
        Assert.DoesNotContain("switch (", code);
    }

    [Fact]
    public void SwitchWithConstLocalCaseLabel_ConvertsToIfElse()
    {
        // C# const local variables used as switch case labels should convert to if-else
        // since Java doesn't allow non-final local variables as case labels
        var result = Convert("""
class Demo {
    public static int Match(int value)
    {
        const int ftpMask = 'f' << 16 | 't' << 8 | 'p';
        const int wssMask = 'w' << 16 | 's' << 8 | 's';
        switch (value)
        {
            case ftpMask: return 1;
            case wssMask: return 2;
            default: return 0;
        }
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Should use if-else instead of switch since case labels are local variables
        Assert.Contains("if (", code);
        Assert.Contains("else if (", code);
        // Should NOT contain switch statement
        Assert.DoesNotContain("switch (", code);
    }

    [Fact]
    public void PointerCastToPointer_DoesNotAddMemorySegmentCast()
    {
        // C# pointer-to-pointer cast should be a no-op in Java since all pointers are MemorySegment
        var result = Convert("""
using System;

class Demo {
    public static unsafe void Test(long* lptr, int offset)
    {
        char c = *(char*)(lptr + 1);
        int i = *(int*)(lptr + 1);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Should NOT contain (MemorySegment) cast on pointer dereference results
        Assert.DoesNotContain("(MemorySegment)", code);
        // Should contain MemorySegment.get() calls
        Assert.Contains("get(ValueLayout.JAVA_CHAR", code);
        Assert.Contains("get(ValueLayout.JAVA_INT", code);
    }

    [Fact]
    public void SwitchWithGotoCase_HasFallbackReturnAfterLoop()
    {
        // Goto-case switch in a non-void method should have a fallback return
        // after the while loop to satisfy Java's definite assignment analysis
        var result = Convert("""
class Demo {
    public static string GetValue(int mode)
    {
        switch (mode)
        {
            case 1:
                return "one";
            case 2:
                goto case 1;
            default:
                return null;
        }
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Should contain a fallback return after the while loop
        Assert.Contains("return null;", code);
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
