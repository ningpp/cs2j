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
