using System.IO;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C06 R8 Java 半区发射测试：带 [Cs2j.Generation.InteropFacade] 标记的
/// C# stub 经 MethodTransformer 发射为 MethodHandle 绑定字段 +
/// invokeExact 包装（java.lang.foreign FFM）。
/// </summary>
public class FfmFacadeEmissionUnitTests
{
    private const string SupportSource = """
        namespace Cs2j.Generation
        {
            [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = false)]
            internal sealed class InteropFacadeAttribute : global::System.Attribute
            {
                public InteropFacadeAttribute(string library, string entryPoint)
                {
                    Library = library;
                    EntryPoint = entryPoint;
                }

                public string Library { get; }
                public string EntryPoint { get; }
                public string StringEncoding { get; set; } = "utf8";
            }
        }
        """;

    private static (string Java, ConversionContext Context) TransformFacadeClass(string classSource)
    {
        var source = SupportSource + "\n" + classSource;
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var references = new List<MetadataReference>();
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }
        else
        {
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        var compilation = CSharpCompilation.Create("ffm-emit",
            new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var context = new ConversionContext(new ConversionOptions(),
            new TypeMappingRegistry(new TypeMappingConfig()))
        {
            ProjectCompilation = compilation,
            SemanticModel = compilation.GetSemanticModel(tree),
        };

        var classNode = tree.GetCompilationUnitRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.Text == "NativeMethods");

        var javaType = new ClassTransformer().Transform(classNode, context);
        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(javaType);
        return (cu.ToString(""), context);
    }

    [Fact]
    public void PrimitiveFacade_EmitsMethodHandleField_AndInvokeExactWrapper()
    {
        var (java, context) = TransformFacadeClass("""
            class NativeMethods
            {
                [global::Cs2j.Generation.InteropFacade("kernel32", "Beep")]
                public static int Beep(int frequency, int duration)
                    => throw new global::System.PlatformNotSupportedException("stub");
            }
            """);

        Assert.Contains("private static final MethodHandle __cs2jFfm_Beep_2_",
            java, StringComparison.Ordinal);
        Assert.Contains(
            "FfmImports.bind(\"kernel32\", \"Beep\", " +
            "FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.JAVA_INT, ValueLayout.JAVA_INT))",
            java, StringComparison.Ordinal);
        Assert.Contains("invokeExact(frequency, duration)", java, StringComparison.Ordinal);
        Assert.Contains("return (int)", java, StringComparison.Ordinal);
        Assert.Contains("public static int beep(int frequency, int duration)",
            java, StringComparison.Ordinal);

        Assert.Contains("io.github.ningpp.compat.FfmImports", context.ImportedTypes);
        Assert.Contains("java.lang.invoke.MethodHandle", context.ImportedTypes);
        Assert.Contains("java.lang.foreign.FunctionDescriptor", context.ImportedTypes);
        Assert.Contains("java.lang.foreign.ValueLayout", context.ImportedTypes);
    }

    [Fact]
    public void StringParameter_Utf8_EmitsSegmentCopy_AndAddressLayout()
    {
        var (java, context) = TransformFacadeClass("""
            class NativeMethods
            {
                [global::Cs2j.Generation.InteropFacade("ucrtbase", "puts")]
                public static int Puts(string text)
                    => throw new global::System.PlatformNotSupportedException("stub");
            }
            """);

        Assert.Contains("MemorySegment __cs2jSeg_text = FfmImports.utf8(text);",
            java, StringComparison.Ordinal);
        Assert.Contains("FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS)",
            java, StringComparison.Ordinal);
        Assert.Contains("invokeExact(__cs2jSeg_text)", java, StringComparison.Ordinal);
        Assert.Contains("java.lang.foreign.MemorySegment", context.ImportedTypes);
    }

    [Fact]
    public void StringParameter_Utf16NamedArgument_EmitsUtf16Copy()
    {
        var (java, _) = TransformFacadeClass("""
            class NativeMethods
            {
                [global::Cs2j.Generation.InteropFacade("kernel32", "FormatMessageW", StringEncoding = "utf16")]
                public static int FormatMsg(int flags, string text)
                    => throw new global::System.PlatformNotSupportedException("stub");
            }
            """);

        Assert.Contains("FfmImports.utf16(text)", java, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteAndUShortParameters_NarrowToAbiCarriers()
    {
        var (java, _) = TransformFacadeClass("""
            class NativeMethods
            {
                [global::Cs2j.Generation.InteropFacade("x", "Mix")]
                public static int Mix(byte a, ushort b)
                    => throw new global::System.PlatformNotSupportedException("stub");
            }
            """);

        // C# byte/ushort 常规映射为 Java int；ABI 边界窄化到 byte/char 载体。
        Assert.Contains("(byte) a", java, StringComparison.Ordinal);
        Assert.Contains("(char) b", java, StringComparison.Ordinal);
        Assert.Contains("ValueLayout.JAVA_BYTE", java, StringComparison.Ordinal);
        Assert.Contains("ValueLayout.JAVA_CHAR", java, StringComparison.Ordinal);
    }

    [Fact]
    public void VoidReturn_EmitsOfVoidDescriptor()
    {
        var (java, _) = TransformFacadeClass("""
            class NativeMethods
            {
                [global::Cs2j.Generation.InteropFacade("kernel32", "Sleep")]
                public static void Sleep(int milliseconds)
                    => throw new global::System.PlatformNotSupportedException("stub");
            }
            """);

        Assert.Contains(
            "FunctionDescriptor.ofVoid(ValueLayout.JAVA_INT)",
            java, StringComparison.Ordinal);
        Assert.Contains("invokeExact(milliseconds);", java, StringComparison.Ordinal);
        Assert.DoesNotContain("return (", java, StringComparison.Ordinal);
    }

    [Fact]
    public void SameArityOverloads_GetDistinctHandleNames()
    {
        var (java, _) = TransformFacadeClass("""
            class NativeMethods
            {
                [global::Cs2j.Generation.InteropFacade("x", "M1")]
                public static int Calc(int a)
                    => throw new global::System.PlatformNotSupportedException("stub");
                [global::Cs2j.Generation.InteropFacade("x", "M2")]
                public static int Calc(long a)
                    => throw new global::System.PlatformNotSupportedException("stub");
            }
            """);

        var handleLines = java.Split('\n')
            .Where(l => l.Contains("MethodHandle __cs2jFfm_Calc_1_", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, handleLines.Count);
        Assert.Equal(2, handleLines.Select(l => l.Trim()).Distinct().Count());
    }
}
