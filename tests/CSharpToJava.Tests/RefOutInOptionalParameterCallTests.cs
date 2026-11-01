using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0206 in the ref/out/in pass (SDK json
/// <c>Utf8JsonWriter.WriteProperties.Bytes.cs</c>):
/// <c>return Utf8Formatter.TryFormat(value, destination, out bytesWritten);</c>.
/// <c>Utf8Formatter.TryFormat</c> keeps its native <c>out int</c> signature and
/// declares a trailing OPTIONAL <c>format</c> parameter, so the call passes 3
/// arguments against 4 parameters. The external-callee check used to require an
/// exact arity match and therefore skipped lifting, leaving
/// <c>out bytesWritten.Value</c> against the native signature (CS0206).
/// </summary>
public class RefOutInOptionalParameterCallTests
{
    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
                public static int ReadbackInt(ByRef<uint> slot) => (int)slot.Value;
                public static int ReadbackInt(ByRef<byte> slot) => slot.Value;
                public static short ReadbackShort(ByRef<short> slot) => slot.Value;
                public static short ReadbackShort(ByRef<ushort> slot) => (short)slot.Value;
                public static byte ReadbackByte(ByRef<sbyte> slot) => (byte)slot.Value;
                public static long ReadbackLong(ByRef<long> slot) => slot.Value;
                public static long ReadbackLong(ByRef<ulong> slot) => (long)slot.Value;
                public static char ReadbackChar(ByRef<char> slot) => slot.Value;
                public static bool ReadbackBool(ByRef<bool> slot) => slot.Value;
                public static float ReadbackFloat(ByRef<float> slot) => slot.Value;
                public static double ReadbackDouble(ByRef<double> slot) => slot.Value;
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override ref T Value => ref _slot;
            }
        }
        """;

    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static RefOutInRewriterResult Run(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-optional-arg-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static CSharpCompilation CompileOutput(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        return CSharpCompilation.Create(
            "roi-optional-arg-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static void AssertOutputCompiles(string code)
    {
        var errors = CompileOutput(code).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    private const string Utf8FormatterSource = """
        public static class FormatHelpers
        {
            public static bool FormatDouble(double value, System.Span<byte> destination, ref int bytesWritten)
            {
                return System.Buffers.Text.Utf8Formatter.TryFormat(value, destination, out bytesWritten);
            }

            public static string Run()
            {
                int written = -1;
                var buffer = new byte[64];
                var ok = FormatDouble(12345.0, buffer, ref written);
                return ok ? written + ":" + System.Text.Encoding.UTF8.GetString(buffer, 0, written) : "fail";
            }
        }
        """;

    [Fact]
    public void External_Out_Call_With_Omitted_Optional_Argument_Is_Lifted()
    {
        var result = Run(Utf8FormatterSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        // The native signature is kept, so the holder must not be forwarded as
        // `out bytesWritten.Value` (CS0206); the lifted temp is passed instead
        // and written back after the call.
        Assert.DoesNotContain("out bytesWritten.Value", result.OutputCode);
        Assert.Contains("bytesWritten.Value = ", result.OutputCode);

        // Executed behavior: the formatted length and the UTF-8 payload still
        // flow out of the callee and back into the holder.
        var compilation = CompileOutput(result.OutputCode);
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var actual = (string?)asm.GetType("FormatHelpers")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
        Assert.Equal("5:12345", actual);
    }
}
