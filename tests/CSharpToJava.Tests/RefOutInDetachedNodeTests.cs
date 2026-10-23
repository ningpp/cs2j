using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for the conversion-time crash
/// <c>ArgumentException: Syntax node is not within syntax tree</c> thrown by
/// <c>LiftExpressionLambdaBody</c> on the xml-tiny
/// <c>ReflectionXmlSerializationReader.cs</c> shape
/// <c>member.XmlnsSource = (ns, name) =&gt; xmlns.Add(ns, name);</c> inside a
/// for/if nest.
/// <para>
/// <c>VisitParenthesizedLambdaExpression</c> always rebuilds the parameter
/// list via <c>WithParameterList(SeparatedList(...))</c>. For a MULTI-parameter
/// lambda the rebuilt separators drop the original trivia (<c>", "</c> becomes
/// <c>","</c>), so the rebuilt node is detached and its span no longer maps
/// back into the bound tree; the unguarded <c>GetTypeInfo(node)</c> that
/// decides whether the delegate returns void then threw. A holder parameter
/// (<c>ref int x</c> -> <c>ByRef&lt;int&gt; x</c>) shifts the span the same
/// way. Zero- and single-parameter lambdas escaped because no separator is
/// rebuilt (Roslyn's span-based detached-node resolution still found them).
/// The converted delegate type is now queried on the ORIGINAL tree-owned
/// lambda before the parameter list is rebuilt.
/// </para>
/// </summary>
public class RefOutInDetachedNodeTests
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

    // Assign is the exact crashing shape: a two-parameter expression lambda
    // (a comma separator with trivia is rebuilt) whose body has NO liftable
    // ref/out call, assigned inside a for/if nest.
    // NonVoid/Void add a liftable ref/out call behind MULTI-parameter lists so
    // the return-vs-expression-statement block conversion itself runs on a
    // rebuilt (potentially detached) lambda node.
    // The unrelated ref method makes the unit dirty for the rewriter.
    private const string Source = """
        using System;
        using System.Collections.Generic;

        public class Member
        {
            public Action<string, string> XmlnsSource;
        }

        public static class Fake
        {
            public static char HexUnescape(string pattern, ref int index)
            {
                index += 2;
                return pattern[1];
            }

            public static void Consume(string s, ref int index)
            {
                index++;
            }
        }

        public static class Demo
        {
            public static string AssignThroughLambda()
            {
                var member = new Member();
                var xmlns = new Dictionary<string, string>();
                string[] mappings = { "a", "b" };
                for (int i = 0; i < mappings.Length; i++)
                {
                    if (i == 0)
                    {
                        member.XmlnsSource = (ns, name) => xmlns.Add(ns, name);
                    }
                }
                member.XmlnsSource("k", "v");
                return xmlns["k"];
            }

            public static int NonVoidLiftWithParameters(string pattern, int seed)
            {
                int index = seed;
                Func<string, int, object> f = (text, ignored) => Fake.HexUnescape(text, ref index);
                object boxed = f(pattern, 0);
                return index + (char)boxed;
            }

            public static int VoidLiftWithParameters(int seed)
            {
                int index = seed;
                Action<string, object> a = (s, _) => Fake.Consume(s, ref index);
                a("x", null);
                return index;
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
            "roi-detached-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var result = new RefOutInRewriter().Rewrite(Source, model);
        return (compilation, result.OutputCode);
    }

    [Fact]
    public void Multi_Parameter_Expression_Lambda_Does_Not_Throw_And_Lifts_Calls()
    {
        var (_, output) = Rewrite();

        // The non-liftable two-parameter lambda is preserved verbatim.
        Assert.Contains("xmlns.Add(ns, name)", output);
        // Liftable ref arguments in rebuilt-node lambdas are still hoisted:
        // no raw `ref index` survives and the holder rewrite is present.
        Assert.DoesNotContain("ref index)", output);
        Assert.Contains("ByRef<int>", output);

        var outputTree = CSharpSyntaxTree.ParseText(output, new CSharpParseOptions(LanguageVersion.Latest));
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var verify = CSharpCompilation.Create(
            "roi-detached-emit-" + Guid.NewGuid().ToString("N"),
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
    public void Rewritten_Lambdas_Keep_Runtime_Behavior()
    {
        var (_, output) = Rewrite();
        var outputTree = CSharpSyntaxTree.ParseText(output, new CSharpParseOptions(LanguageVersion.Latest));
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var verify = CSharpCompilation.Create(
            "roi-detached-run-" + Guid.NewGuid().ToString("N"),
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

        // The two-parameter assignment lambda stored in the delegate runs.
        var assigned = demo.GetMethod("AssignThroughLambda",
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        Assert.Equal("v", assigned);

        // seed 0: HexUnescape advances index to 2 and returns pattern[1]='7' (55).
        var nonVoid = demo.GetMethod("NonVoidLiftWithParameters",
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { "%75", 0 });
        Assert.Equal(57, nonVoid);

        // seed 5: void delegate path advances index to 6.
        var voidResult = demo.GetMethod("VoidLiftWithParameters",
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { 5 });
        Assert.Equal(6, voidResult);
    }
}
