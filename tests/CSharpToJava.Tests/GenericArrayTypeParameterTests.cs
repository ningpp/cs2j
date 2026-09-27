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

        Assert.Contains("public <T> Object repeat(T value, int count, Class<?> clazz)", code);
        Assert.Contains("TypeHelper.newArrayInstance(clazz, count)", code);
        Assert.Contains("java.lang.reflect.Array.set(result", code);
        Assert.DoesNotContain("new Object[count]", code);
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

        Assert.Contains("private Class<?> kClass;", code);
        Assert.Contains("private Class<?> vClass;", code);
        Assert.Contains("public Demo(Class<?> kClass, Class<?> vClass)", code);
        Assert.Contains("this.kClass = kClass;", code);
        Assert.Contains("this.vClass = vClass;", code);
        Assert.Contains("TypeHelper.newArrayInstance(kClass, count)", code);
        Assert.Contains("TypeHelper.newArrayInstance(vClass, count)", code);
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
        Assert.Contains("this.items = (T[]) TypeHelper.newArrayInstance(tClass, 8);", code);
        Assert.Contains("new Stack<String>(String.class)", code);
        Assert.DoesNotContain("private T[] items = (T[]) TypeHelper.newArrayInstance(tClass, 8);", code);
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
        Assert.Contains("private Class<?> tClass;", code);
        Assert.Contains("public ArrayBuilder(int capacity, Class<?> tClass)", code);
        // Array creation should use tClass
        Assert.Contains("TypeHelper.newArrayInstance(tClass", code);
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
        Assert.Contains("(char) MemorySegmentHelper.getChar(", code);
        Assert.Contains("MemorySegmentHelper.getInt(", code);
    }

    [Fact]
    public void GetElementType_OnPrimitiveArray_UsesTypeHelperBoxedComponentType()
    {
        // C# typeof(int[]).GetElementType() should convert to TypeHelper.getElementType(int[].class)
        // which returns Integer.class (not int.class), so comparisons like itemTypeDst == Integer.class work.
        var result = Convert("""
class Demo {
    public void Process(int[] values)
    {
        var elemType = values.GetType().GetElementType();
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("TypeHelper.getElementType", code);
        Assert.DoesNotContain("getComponentType", code);
    }

    [Fact]
    public void TypeOfTypeParameterInGenericArrayMethod_WrapsPrimitiveClassLiteralForComparison()
    {
        // C# typeof(T) inside a generic method represents the runtime Type, which for
        // primitive T must be the wrapper class literal (Integer.class) so comparisons
        // like destinationType == typeof(int) match. The array creation call site uses
        // int.class, but inside the method typeof(T) must be TypeHelper.toWrapperType(clazz).
        var result = Convert("""
class Demo {
    private static readonly Type s_int32Type = typeof(int);

    public T[] ToArray<T>(string[] values)
    {
        T[] arr = new T[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (typeof(T) == s_int32Type)
                arr[i] = (T)(object)int.Parse(values[i]);
        }
        return arr;
    }

    public int[] Run()
    {
        return ToArray<int>(new[] { "1", "2" });
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public <T> Object toArray(String[] values, Class<?> clazz)", code);
        Assert.Contains("TypeHelper.toWrapperType(clazz)", code);
        Assert.DoesNotContain("typeof(T)", code);
        Assert.DoesNotContain("T.class", code);
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

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx"))
                && Directory.Exists(Path.Combine(dir, "java", "csharptojava-compat")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
