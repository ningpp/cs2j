using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0266 (object -> bool) in the ref/out/in pass: a
/// conditional expression with NO liftable ref/out call anywhere in its
/// condition or branches was nevertheless statement-spliced into an
/// untyped <c>object __condN = default!;</c> temp, so a value-typed
/// consumer such as <c>bool isSet = __condN;</c> failed to compile.
/// Observed in RegexCompiler's string.Create bitmap lambda with
/// <c>state.Item2 ? CharInClass(...) : CharInClass(...)</c>.
/// </summary>
public class RefOutInLiftFreeConditionalTests
{
    private const string Source = """
        using System;

        internal static class RegexCharClass
        {
            public static bool CharInClass(char ch, string set, ref int[]? cache) => true;
            public static bool CharInClass(char ch, string set) => ch == 'a';
        }

        public static class Demo
        {
            public static string Make(bool invariant, string charClass)
            {
                string bitVectorString = string.Create(8, new ValueTuple<string, bool>(charClass, invariant), (dest, state) =>
                {
                    for (int i = 0; i < 128; i++)
                    {
                        char c = (char)i;
                        bool isSet = state.Item2 ?
                            RegexCharClass.CharInClass(char.ToLowerInvariant(c), state.Item1) :
                            RegexCharClass.CharInClass(c, state.Item1);
                        if (isSet)
                        {
                            dest[i >> 4] = '1';
                        }
                    }
                });
                return bitVectorString;
            }
        }
        """;

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

    [Fact]
    public void Lift_Free_Conditional_Inside_Lambda_Is_Not_Spliced()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "roi-cond-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);

        var result = new RefOutInRewriter().Rewrite(Source, model);

        // The ternary must stay an expression: no untyped object temp and no
        // statement split (Changed may still be true due to lambda fragment
        // node reconstruction, but the shape is preserved).
        Assert.DoesNotContain("__cond", result.OutputCode);
        Assert.Contains("state.Item2 ?", result.OutputCode);

        // The rewritten output itself must compile and run correctly.
        var rewrittenTree = CSharpSyntaxTree.ParseText(result.OutputCode,
            new CSharpParseOptions(LanguageVersion.Latest));
        var supportTree = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var verify = CSharpCompilation.Create(
            "roi-cond-emit-" + Guid.NewGuid().ToString("N"),
            new[] { rewrittenTree, supportTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        using var ms = new MemoryStream();
        var emit = verify.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var make = asm.GetType("Demo")!.GetMethod("Make", BindingFlags.Public | BindingFlags.Static)!;
        var output = (string)make.Invoke(null, new object[] { false, "abc" })!;
        Assert.Equal(8, output.Length);
        Assert.All(output, ch => Assert.True(ch == '\0' || ch == '1'));
    }
}
