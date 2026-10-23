using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for residual CS1615 sites such as
/// <c>Assert.Throws&lt;T&gt;(() =&gt; Uri.HexUnescape("%75", ref index))</c>:
/// a liftable ref/out call inside an EXPRESSION-bodied lambda had no
/// statement node the statement lifter could splice, so the raw
/// <c>ref index</c> survived against the rewritten ByRef signature.
/// The expression body is now converted to a block body with the slot
/// hoisted inside the lambda and a trailing return for non-void delegates.
/// </summary>
public class RefOutInExpressionLambdaBodyTests
{
    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract T Value { get; set; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
                public static ByRef<T> Create(System.Func<T> getter, System.Action<T> setter)
                    => new AccessorByRef<T>(getter, setter);
                // out 值类型读回（与 src/Cs2jRefOutIn/ByRef.cs 的 ReadbackX 一致，Java 半区 null→default 桥）
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
                public override T Value { get => _slot; set => _slot = value; }
            }
            internal sealed class AccessorByRef<T> : ByRef<T>
            {
                private readonly System.Func<T> _getter;
                private readonly System.Action<T> _setter;
                public AccessorByRef(System.Func<T> getter, System.Action<T> setter)
                { _getter = getter; _setter = setter; }
                public override T Value { get => _getter(); set => _setter(value); }
            }
        }
        """;

    private const string Source = """
        using System;

        public class FakeUri
        {
            public static char HexUnescape(string pattern, ref int index)
            {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                index += 2;
                return pattern[1];
            }

            public static bool TryCreate(string s, int kind, out FakeUri result)
            {
                result = null;
                return false;
            }

            public static void Consume(string s, ref int index)
            {
                index++;
            }
        }

        public static class ExceptionAssert
        {
            public static T ThrowsT<T>(Func<object> action) where T : Exception => (T)action();
            public static void Void(Action action) => action();
        }

        public static class Demo
        {
            public static void NonVoidCapturedRef(int index)
            {
                ExceptionAssert.ThrowsT<ArgumentOutOfRangeException>(() => FakeUri.HexUnescape("%75", ref index));
            }

            public static void VoidCapturedRef(int index)
            {
                ExceptionAssert.Void(() => FakeUri.Consume("x", ref index));
            }

            public static void OutInLambda()
            {
                ExceptionAssert.ThrowsT<Exception>(() =>
                {
                    FakeUri u;
                    return FakeUri.TryCreate("s", 0, out u) ? (object)new Exception() : new Exception();
                });
            }
        }
        """;

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

    private static (Compilation Original, string Output) Rewrite()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "roi-explambda-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var result = new RefOutInRewriter().Rewrite(Source, model);
        return (compilation, result.OutputCode);
    }

    [Fact]
    public void Liftable_Call_Inside_Expression_Lambda_Is_Block_Body_Lifted()
    {
        var (_, output) = Rewrite();

        // No raw ref/out argument may survive against the rewritten signature.
        Assert.DoesNotContain("ref index)", output);
        Assert.Contains("ByRef<int>", output);

        var outputTree = CSharpSyntaxTree.ParseText(output, new CSharpParseOptions(LanguageVersion.Latest));
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var verify = CSharpCompilation.Create(
            "roi-explambda-emit-" + Guid.NewGuid().ToString("N"),
            new[] { outputTree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = verify.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }

    [Fact]
    public void Non_Void_Lambda_Block_Returns_Call_Result_At_Runtime()
    {
        var (_, output) = Rewrite();
        var outputTree = CSharpSyntaxTree.ParseText(output, new CSharpParseOptions(LanguageVersion.Latest));
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var verify = CSharpCompilation.Create(
            "roi-explambda-run-" + Guid.NewGuid().ToString("N"),
            new[] { outputTree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = verify.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var demo = asm.GetType("Demo")!;

        // index = -1 forces HexUnescape to throw ArgumentOutOfRangeException.
        var ex = Assert.Throws<TargetInvocationException>(() =>
            demo.GetMethod("NonVoidCapturedRef", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { -1 }));
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);

        // Void lambda path executes without throw.
        demo.GetMethod("VoidCapturedRef", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { 0 });

        // out-in-lambda path completes without throw.
        demo.GetMethod("OutInLambda", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
    }
}
